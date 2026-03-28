using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace EpointPlugin
{
    // =========================================================================
    // [业务逻辑中心] 负责将所有的计算、指令判断、事件监听
    // =========================================================================
    public static class EpointSystem
    {
        private static readonly Random Rand = new Random(); // 随机数生成器（用于运气波动计算）
        private static System.Timers.Timer? _onlineTimer;   // 负责定时发放在线奖励的时钟
        private static System.Timers.Timer? _autoSignInTimer;
        
        // 【风格优化】根据 C# 静态只读字段命名规范，去掉下划线并首字母大写
        private static readonly Dictionary<string, int> SessionTime = new Dictionary<string, int>(); 
        private static readonly Dictionary<int, Dictionary<string, int>> BossDamageTracker = new Dictionary<int, Dictionary<string, int>>(); 
        private static readonly Dictionary<string, Dictionary<int, int>> PersonalKillTracker = new Dictionary<string, Dictionary<int, int>>();

        // 事件 Boss 白名单 ID
        private static readonly HashSet<int> MiniBossIds = new HashSet<int>
        {
            439, // 荷兰飞盗船
            392, // 火星飞碟核心
            315, // 南瓜王
            325, // 哀木
            345, // 冰霜女王
            344, // 圣诞坦克
            346, // 常绿尖叫怪
            477, // 蛾怪
            493, 507, 422, 517, // 拜月教四柱
            564, 565 // 撒旦军队相关
        };
        
        // ================= 盲盒配置 =================
        private class BlindBoxItem
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
            public int IconItemId { get; init; } // 商店UI里展示的宝箱图标
            public string ColorHex { get; init; } = "FFFFFF"; // 专属箱子颜色
            public int DefaultPrice { get; init; } // 预设价格
            public List<int> DropPool { get; init; } = new List<int>(); 

            // 调用 Price 时，优先从 Config 读取；若无配置，退回 DefaultPrice。
            public int Price => Epoint.Config.BlindBoxPrices.TryGetValue(Id, out int p) ? p : DefaultPrice;
        }

        private static readonly List<BlindBoxItem> BlindBoxes = new List<BlindBoxItem>
        {
            new BlindBoxItem { Id = 101, Name = "宝箱怪盲盒", IconItemId = 306, ColorHex = "FFD700", DefaultPrice = 2000, DropPool = new List<int> { 437, 517, 536, 535, 554, 532 } }, 
            new BlindBoxItem { Id = 102, Name = "冰雪宝箱怪盲盒", IconItemId = 681, ColorHex = "A5F2F3", DefaultPrice = 2000, DropPool = new List<int> { 727, 726, 679, 1312 } },
            new BlindBoxItem { Id = 103, Name = "腐化宝箱怪盲盒", IconItemId = 1529, ColorHex = "9400D3", DefaultPrice = 2000, DropPool = new List<int> { 3014, 3012, 3013, 3015, 3011 } },
            new BlindBoxItem { Id = 104, Name = "猩红宝箱怪盲盒", IconItemId = 1530, ColorHex = "DC143C", DefaultPrice = 2000, DropPool = new List<int> { 3008, 3010, 3009, 3016, 3007 } },
            new BlindBoxItem { Id = 105, Name = "神圣宝箱怪盲盒", IconItemId = 1531, ColorHex = "FFB6C1", DefaultPrice = 2000, DropPool = new List<int> { 3029, 3020, 3023, 3030 } },
            new BlindBoxItem { Id = 106, Name = "丛林宝箱怪盲盒", IconItemId = 1528, ColorHex = "32CD32", DefaultPrice = 2000, DropPool = new List<int> { 5360, 4953, 5324 } }
        };
        
        // ================= 模块一：定时器管理 =================
        
        // 初始化在线时长奖励时钟
        public static void InitializeTimer()
        {
            _onlineTimer = new System.Timers.Timer(Epoint.Config.OnlineRewardInterval * 60 * 1000); // 转换时间单位为毫秒
            _onlineTimer.Elapsed += OnOnlineTimerElapsed; // 绑定到点后触发的事件
            _onlineTimer.AutoReset = true; // 允许无限循环跳动
            _onlineTimer.Start();
            
            // 每 60 秒执行一次全服静默查岗
            _autoSignInTimer = new System.Timers.Timer(60000);
            // 使用弃元 "_" 忽略不需要的参数
            _autoSignInTimer.Elapsed += (_, _) =>
            {
                foreach (var player in TShock.Players)
                {
                    if (player is { Active: true, IsLoggedIn: true })
                    {
                        TryDailySignIn(player, player.Account.Name);
                    }
                }
            };
            _autoSignInTimer.AutoReset = true;
            _autoSignInTimer.Start();
        }

        // 销毁时钟（卸载插件时清理内存）
        public static void Dispose()
        {
            _onlineTimer?.Stop();
            _onlineTimer?.Dispose();
            // 释放心跳时钟
            _autoSignInTimer?.Stop();
            _autoSignInTimer?.Dispose();
        }
        
        // 修改配置文件后，重启时钟让新时间生效
        public static void ReloadTimer()
        {
            if (_onlineTimer != null)
            {
                _onlineTimer.Interval = Epoint.Config.OnlineRewardInterval * 60 * 1000;
            }
        }

        // ================= 补丁：自动签到模块 =================
        // 通过玩家行为校验是否发放签到奖励
        private static void TryDailySignIn(TSPlayer player, string accountName, bool delayMessage = false)
        {
            var data = Epoint.Data.GetPlayerData(accountName);

            string todayStr = DateTime.Now.ToString("yyyy-MM-dd");
            if (data.LastLoginDate == todayStr) return;

            int newTotalDays = data.TotalDays + 1;
            int newStreakDays = 1;

            // 连签天数计算
            if (!string.IsNullOrEmpty(data.LastLoginDate) && DateTime.TryParse(data.LastLoginDate, out DateTime lastDate))
            {
                if ((DateTime.Now.Date - lastDate.Date).Days == 1) newStreakDays = data.StreakDays + 1;
            }

            // 每日签到积分奖励计算
            int effectiveDaysForCap = Math.Max(0, data.TotalDays); // 累计登录天数
            double daysCoeff = 1.0 + (effectiveDaysForCap * 0.05); // 累计登录系数
            
            int baseCap = Epoint.Config.FastPacedMode ? Epoint.Config.BaseDailyCap * 2 : Epoint.Config.BaseDailyCap; // 每日获取积分上限基数
            int dailyCap = (int)(baseCap * daysCoeff); // 每日获取积分上限
            int baseSignInReward = (int)(dailyCap * 0.5); // 每日签到积分基数
            
            double streakCoeff = Math.Min(1.0 + (newStreakDays * 0.01), 1.15); // 连签积分奖励加成，最高 1.15 倍
            double luck = Rand.NextDouble() * 0.4 + 0.8; // 随机波动 80%~120%

            int theoreticalReward = (int)(baseSignInReward * streakCoeff * luck); // 每日签到奖励积分
            int actualReward = Math.Min(theoreticalReward, dailyCap); // 防止每日签到奖励积分超过上限

            // 结算并保存到硬盘
            data.Points += actualReward;
            data.PointsToday = actualReward; // 玩家每日首次登录后重置当日积分获取量
            data.TotalDays = newTotalDays;
            data.StreakDays = newStreakDays;
            data.LastLoginDate = todayStr;
            Epoint.Data.SavePlayerData(data);

            // 使用本地函数消除内存分配开销
            void SendMessage()
            {
                if (player.Active)
                {
                    string msg;
                    if (luck > 1.15)
                        msg = $"[c/00FF00:你感觉今天运气好极了！｡:.ﾟヽ(*´∀`)ﾉﾟ.:｡] 已连续签到 [c/FFD700:{newStreakDays}] [c/FFFFFF:天]，今日奖励 [c/FFD700:{actualReward}] ep";
                    else if (luck < 0.85)
                        msg = $"[c/00FF00:似乎今天运气有点差 (☍﹏⁰。)] 已连续签到 [c/FFD700:{newStreakDays}] [c/FFFFFF:天]，今日奖励 [c/FFD700:{actualReward}] ep";
                    else
                        msg = $"[c/00FF00:签到成功 ～(∠・ω< )⌒★] 已连续签到 [c/FFD700:{newStreakDays}] [c/FFFFFF:天]，今日奖励 [c/FFD700:{actualReward}] ep";

                    player.SendMessage(msg, 255, 255, 255);

                    if (actualReward < theoreticalReward) 
                        player.SendErrorMessage("今日获取积分已达上限！"); 
                }
            }

            // 刚进服延迟2秒发消息，防止跟TShock自带的欢迎开场白糊在一起
            if (delayMessage)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    SendMessage();
                });
            }
            else
            {
                SendMessage();
            }
        }

        // ================= 模块二：插件指令处理 =================
        
        // 帮助菜单模块
        public static void EpHelpCommand(CommandArgs args)
        {
            args.Player.SendInfoMessage("==================== [c/FFD700:Epoint 积分插件帮助菜单] ====================");
            args.Player.SendMessage("/epinfo [c/FFFFFF:- 查看个人积分档案与签到状态]", 85, 210, 132);
            args.Player.SendMessage("/epshop [页码](留空则取1) [c/FFFFFF:- 打开积分商店 (第 1 页道具商店，第 2 页盲盒商店)]", 85, 210, 132);
            args.Player.SendMessage("/epbuy <商品序号> [购买次数](留空则取1) [c/FFFFFF:- 购买商品]", 85, 210, 132);
            args.Player.SendMessage("[c/55CDFF:积分获取途径：][c/FFFFFF:每日首次登录、累积在线时长、击败Boss奖励、击杀小怪里程碑奖励。]", 85, 210, 132);
        }

        // 计算文字的视觉宽度以便排版
        private static int GetDisplayWidth(string str)
        {
            return str.Sum(c => c > 255 ? 2 : 1);
        }

        // 查询个人档案模块
        public static void EpInfoCommand(CommandArgs args)
        {
            if (!args.Player.Active || !args.Player.IsLoggedIn)
            {
                args.Player.SendErrorMessage("[Epoint] 请先登录");
                return;
            }

            string accountName = args.Player.Account.Name;
            TryDailySignIn(args.Player, accountName); // 查岗拦截
            
            var data = Epoint.Data.GetPlayerData(accountName);

            // 动态计算玩家今日的积分上限
            int effectiveDays = Math.Max(0, data.TotalDays - 1);
            int baseCap = Epoint.Config.FastPacedMode ? Epoint.Config.BaseDailyCap * 2 : Epoint.Config.BaseDailyCap;
            int dailyCap = (int)(baseCap * (1 + effectiveDays * 0.05));

            args.Player.SendInfoMessage($"======== [c/87CEEB:{accountName} 的积分档案] ========");
            args.Player.SendSuccessMessage($"[c/FFD700:积分余额:] {data.Points} ep");
            args.Player.SendSuccessMessage($"[c/FFA500:今日获取:] {data.PointsToday} / {dailyCap} ep");
            args.Player.SendInfoMessage($"[c/55CDFF:累积登录:] {data.TotalDays} [c/FFFFFF:天]");
            args.Player.SendInfoMessage($"[c/87CEEB:连续签到:] {data.StreakDays} [c/FFFFFF:天]");
        }

        // 积分商店 UI 模块
        public static void EpShopCommand(CommandArgs args)
        {
            if (!args.Player.Active || !args.Player.IsLoggedIn)
            {
                args.Player.SendErrorMessage("[Epoint] 请先登录");
                return;
            }
            
            TryDailySignIn(args.Player, args.Player.Account.Name); // 查岗拦截

            int page = 1; // 默认第一页
            if (args.Parameters.Count > 0)
            {
                if (!int.TryParse(args.Parameters[0], out page) || page < 1 || page > 2)
                {
                    args.Player.SendErrorMessage("无效的页码。");
                    return;
                }
            }
            
            int currentPoints = Epoint.Data.GetPoints(args.Player.Account.Name);

            if (page == 1) // ============ 第一页：道具商店 ============
            {
                var items = Epoint.Config.ShopItems;
                if (items.Count == 0)
                {
                    args.Player.SendErrorMessage("当前暂无商品上架，请联系管理员。");
                    return;
                }
                
                args.Player.SendInfoMessage("================= [c/FFD700:Epoint 道具商店] =================");

                // 道具全部在第一页渲染
                for (int i = 0; i < items.Count; i += 3) // 控制每行三列
                {
                    string line = "";
                    for (int j = 0; j < 3 && i + j < items.Count; j++) // 控制每行三列
                    {
                        var item = items[i + j];
                        // 展示泰拉瑞亚内置的物品标签，原版格式类似 [i:123] 或 [i/s10:123]（堆叠数量大于 1 时）
                        string itemTag = item.Stack > 1 ? $"[i/s{item.Stack}:{item.ItemNetId}]" : $"[i:{item.ItemNetId}]";
                        // 限购商品序号标红，普通商品序号标黄
                        string idColor = item.IsOneTime ? "FF7E7E" : "FFFFFF";
                        
                        // 构建显示字符串：[序号] 图标 价格 ep
                        string rawDisplayStr = $"({item.Id}) {item.Price} ep"; // 剥除颜色代码计算原始字符串宽度
                        int paddingSpaces = Math.Max(2, 20 - GetDisplayWidth(rawDisplayStr) - 3); // 调整商品文字间距，设置每个商品所占固定宽度（20），图标所占宽度（-3）
                        string itemStr = $"[c/{idColor}:({item.Id})] {itemTag} [c/00FF00:{item.Price} ep]" + new string(' ', paddingSpaces); // 将空格拼在后面
                        line += itemStr;
                    }
                    args.Player.SendMessage(line, 255, 255, 255); 
                }

                args.Player.SendInfoMessage("[c/FF7E7E:(注：红色序号为限购商品)]");
                args.Player.SendInfoMessage("================================================");
                args.Player.SendMessage($"[c/FFFFFF:积分余额：]{currentPoints} ep [c/FFFFFF:|] [c/55CDFF:第 1 页] [c/FFFFFF:(输入] /epshop 2 [c/FFFFFF:查看盲盒商店)]", 85, 210, 132);
            }
            
            else // ============ 第二页：盲盒商店 ============
            {
                args.Player.SendInfoMessage("================= [c/FFD700:Epoint 盲盒商店] =================");
                
                // 进度锁提示
                if (!Main.hardMode)
                {
                    args.Player.SendErrorMessage("[c/FFFFFF:【已锁定】] 本页商品需击败 [c/FF7E7E:[血肉墙]] 后方可购买！");
                }
                
                foreach (var box in BlindBoxes)
                {
                    string drops = string.Join("", box.DropPool.Select(id => $"[i:{id}]"));
                    // 格式: (序号) XX盲盒[图标] XX ep | (可能包含的物品: [图标][图标]...)
                    string line = $"[c/FF7E7E:({box.Id})] [c/{box.ColorHex}:{box.Name}][i:{box.IconItemId}] [c/00FF00:{box.Price} ep] [c/FFFFFF:| (可能包含的物品: ]{drops}[c/FFFFFF:)]";
                    args.Player.SendMessage(line, 255, 255, 255);
                }
                
                args.Player.SendInfoMessage("================================================");
                args.Player.SendMessage($"[c/FFFFFF:积分余额：]{currentPoints} ep [c/FFFFFF:|] [c/55CDFF:第 1 页] [c/FFFFFF:(输入] /epshop 1 [c/FFFFFF:返回积分商店)]", 85, 210, 132);
            }
        }
        
        // 核心购物结算逻辑
        public static void EpBuyCommand(CommandArgs args)
        {
            if (!args.Player.Active || !args.Player.IsLoggedIn)
            {
                args.Player.SendErrorMessage("[Epoint] 请先登录");
                return;
            }
            
            TryDailySignIn(args.Player, args.Player.Account.Name);

            // 校验玩家输入的参数
            if (args.Parameters.Count < 1 || !int.TryParse(args.Parameters[0], out int shopId))
            {
                args.Player.SendErrorMessage("语法错误。正确用法: /epbuy <商品序号> [购买次数]");
                return;
            }

            int buyTimes = 1; // 如果没输入第二参数，默认买1份
            if (args.Parameters.Count > 1 && (!int.TryParse(args.Parameters[1], out buyTimes) || buyTimes <= 0))
            {
                args.Player.SendErrorMessage("无效的购买次数");
                return;
            }

            // 去配置文件和盲盒列表里找找有没有这个商品
            var shopItem = Epoint.Config.ShopItems.FirstOrDefault(i => i.Id == shopId);
            var blindBox = BlindBoxes.FirstOrDefault(b => b.Id == shopId);

            if (shopItem == null && blindBox == null)
            {
                args.Player.SendErrorMessage("无效的商品序号");
                return;
            }

            // 当前世界未击败血肉墙时拦截购买盲盒
            if (blindBox != null && !Main.hardMode)
            {
                args.Player.SendErrorMessage("购买失败：盲盒系列商品需要在击败血肉墙后才能购买！");
                return;
            }
            
            string accountName = args.Player.Account.Name;
            
            // 价格计算
            int price = shopItem?.Price ?? blindBox?.Price ?? 0;
            int totalCost = price * buyTimes;
            
            // 检验限购商品购买次数
            if (shopItem is { IsOneTime: true })
            {
                if (buyTimes > 1) 
                {
                    args.Player.SendErrorMessage($"【[c/B0E0E6:{shopItem.Name}]】只能购买 1 份。");
                    return;
                }
                if (Epoint.Data.HasPurchasedOneTimeItem(accountName, shopItem.ItemNetId)) 
                {
                    args.Player.SendErrorMessage($"购买失败：您已买过【[c/B0E0E6:{shopItem.Name}]】，限购一次。");
                    return;
                }
            }

            // 检查玩家背包是不是满了 (取玩家前 50 格主背包空间)
            int requiredSlots = blindBox != null ? buyTimes : 1; 
            bool hasEmptySpace = args.Player.TPlayer.inventory.Take(50).Count(i => i == null || i.IsAir) >= requiredSlots;
            if (!hasEmptySpace)
            {
                args.Player.SendErrorMessage("购买失败：背包空间不足，请先清理！");
                return;
            }

            // 尝试付款
            if (!Epoint.Data.TryRemovePoints(accountName, totalCost))
            {
                args.Player.SendErrorMessage("购买失败：积分不足！(๑´ㅁ`)");
                return;
            }

            // 购买成功，发放商品
            if (shopItem != null)
            {
                args.Player.GiveItem(shopItem.ItemNetId, shopItem.Stack * buyTimes);
                if (shopItem.IsOneTime) Epoint.Data.RecordPurchase(accountName, shopItem.ItemNetId); // 如果是限购商品，记入限购商品记录池
                args.Player.SendSuccessMessage("[c/00FF00:购买成功！ヽ(^ω^ )]");
            }
            else if (blindBox != null)
            {
                for (int k = 0; k < buyTimes; k++)
                {
                    int dropId = blindBox.DropPool[Rand.Next(blindBox.DropPool.Count)];
                    args.Player.GiveItem(dropId, 1);
                    // 全服广播开盲盒结果
                    TSPlayer.All.SendMessage($"[c/55CDFF:{accountName}] 开启了 [c/{blindBox.ColorHex}:{blindBox.Name}]，获得了 [i:{dropId}]", 255, 255, 255);
                }
            }
        }

        // ================= 模块三：自动事件钩子监听 =================

        // 玩家刚输入完登录密码触发
        public static void OnPlayerLogin(PlayerPostLoginEventArgs args)
        {
            var player = args.Player;
            if (!player.Active) return;

            string accountName = player.Account.Name;
            SessionTime[accountName] = 0; // 重置该玩家的在线挂机时钟

            TryDailySignIn(player, accountName, true); // true 代表延迟2秒发消息
        }
        
        // 累计在线时长定时器到点触发
        private static void OnOnlineTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // 遍历整个服务器里的所有人
            foreach (var player in TShock.Players)
            {
                if (player == null || !player.Active || !player.IsLoggedIn) continue; // 没登录的忽略

                string accountName = player.Account.Name;
                TryDailySignIn(player, accountName);
                
                SessionTime.TryAdd(accountName, 0);
                
                // 给该玩家累加在线时长
                SessionTime[accountName] += Epoint.Config.OnlineRewardInterval;

                var data = Epoint.Data.GetPlayerData(accountName);
                
                int effectiveDays = Math.Max(0, data.TotalDays - 1);
                double daysCoeff = 1.0 + (effectiveDays * 0.05);

                // 保留了快节奏模式的判定，在线积分取决于 Config 中的设定
                int theoreticalReward = Epoint.Config.FastPacedMode ? Epoint.Config.OnlineRewardPoints * 2 : Epoint.Config.OnlineRewardPoints;

                // 计算今日所剩可获得积分额度，防止多发
                int baseCap = Epoint.Config.FastPacedMode ? Epoint.Config.BaseDailyCap * 2 : Epoint.Config.BaseDailyCap;
                int dailyCap = (int)(baseCap * daysCoeff);
                int remainingCap = dailyCap - data.PointsToday;
                
                if (remainingCap <= 0) continue; 
                
                int actualReward = Math.Min(theoreticalReward, remainingCap);

                if (actualReward > 0)
                {
                    Epoint.Data.AddPoints(accountName, actualReward);
                    
                    data.PointsToday += actualReward;
                    Epoint.Data.SavePlayerData(data); 
                    
                    string capSuffix = (data.PointsToday >= dailyCap) ? " [c/FF0000:(今日积分已达上限！)]" : "";
                    
                    player.SendSuccessMessage($"[c/55CDFF:叮咚～(∠・ω< )⌒★] [c/87CEEB:在线奖励] [c/FFD700:{actualReward}] ep{capSuffix}");
                }
            }
        }

        // 玩家退出服务器时触发
        public static void OnPlayerLeave(LeaveEventArgs args)
        {
            var player = TShock.Players[args.Who];
            if (player is { Active: true, IsLoggedIn: true })
            {
                // 清理该玩家占用的内存数据字典
                SessionTime.Remove(player.Account.Name);
                PersonalKillTracker.Remove(player.Account.Name);
            }
        }
        
        // 服务器里有 NPC 被打了会触发这个钩子
        public static void OnNpcStrike(object? sender, GetDataHandlers.NPCStrikeEventArgs args)
        {
            var player = args.Player;
            var npc = Main.npc[args.ID];

            if (!player.Active || !player.IsLoggedIn) return;
            if (!npc.active) return;
            if (!npc.boss && !MiniBossIds.Contains(npc.netID)) return; // 忽略既不是原版 Boss 也不在特殊小头目白名单里的敌怪
            
            string accountName = player.Account.Name;
            
            // 在记录伤害时，多部件 Boss 统一映射到其核心本体 ID 上
            int bossKey = npc.realLife >= 0 ? npc.realLife : args.ID;
            
            // 确保这只Boss的字典存在，然后把伤害累加进去
            BossDamageTracker.TryAdd(bossKey, new Dictionary<string, int>());
            BossDamageTracker[bossKey].TryAdd(accountName, 0);
            
            BossDamageTracker[bossKey][accountName] += args.Damage;
        }

        // 服务器里有怪物被击杀了会触发这个钩子
        public static void OnNpcKilled(NpcKilledEventArgs args)
        {
            var npc = args.npc;
            // 获取导致该事件触发的 npc 索引，不用关心是哪个部件死的
            int bossKey = npc.realLife >= 0 ? npc.realLife : npc.whoAmI; // Boss统一ID

            // ---- 情况A：击杀 Boss 或 特殊小头目 ----
            if (npc.boss || MiniBossIds.Contains(npc.netID))
            {
                // Boss 非线性平滑奖励函数
                int baseBossPool = (int)(400 * (1 - Math.Exp(-npc.lifeMax / 15000.0)));
                baseBossPool = Math.Clamp(baseBossPool, 100, 400);
                
                if (Epoint.Config.BossRewardByDamage)
                {
                    // 模式1：根据造成的伤害比例分配积分
                    if (BossDamageTracker.TryGetValue(bossKey, out var dmgDict) && dmgDict.Count > 0)
                    {
                        int totalDamage = dmgDict.Values.Sum();
                        foreach (var kvp in dmgDict)
                        {
                            string accountName = kvp.Key;
                            double damagePercent = (double)kvp.Value / totalDamage;
                            
                            // 遍历在线玩家，找到参战玩家并分配积分
                            var player = TShock.Players.FirstOrDefault(p =>
                                p is { Active: true, IsLoggedIn: true, Account: not null }
                                && p.Account.Name == accountName);
                            if (player != null) GrantBossReward(player, accountName, baseBossPool, damagePercent, npc.FullName);
                        }
                    }
                }
                else
                {
                    // 模式2：只要在线的登录玩家，全部平分奖励积分池
                    var activePlayers = TShock.Players
                        .Where(p => p is { Active: true, IsLoggedIn: true })
                        .ToList();
                    if (activePlayers.Count > 0)
                    {
                        double equalPercent = 1.0 / activePlayers.Count;
                        foreach (var player in activePlayers) GrantBossReward(player, player.Account.Name, baseBossPool, equalPercent, npc.FullName);
                    }
                }
                
                // 积分分配完成，把这只 Boss 的伤害数据从内存删掉
                BossDamageTracker.Remove(bossKey);
                return; 
            }

            // ---- 情况B：击杀小怪(记录里程碑) ----
            // 如果是友好 NPC、低于 5 血的怪、雕像生成的怪，则无视
            if (npc.friendly || npc.lifeMax < 5 || npc.SpawnedFromStatue) return;
            
            int killerIndex = npc.lastInteraction; // 找出是谁补的最后一刀
            if (killerIndex < 0 || killerIndex >= 255) return; // 如果是陷阱杀死的则无视

            var killerPlayer = TShock.Players[killerIndex];
            if (killerPlayer == null || !killerPlayer.Active || !killerPlayer.IsLoggedIn) return;

            string killerAccount = killerPlayer.Account.Name;
            TryDailySignIn(killerPlayer, killerAccount);
            
            int netId = npc.netID;

            // 在该玩家的个人字典里，把这只怪的击杀数 +1
            PersonalKillTracker.TryAdd(killerAccount, new Dictionary<int, int>());
            PersonalKillTracker[killerAccount].TryAdd(netId, 0);

            PersonalKillTracker[killerAccount][netId]++;
            int personalKills = PersonalKillTracker[killerAccount][netId];
            
            // 如果累计击杀了 50 只 (或是 100, 150...)
            if (personalKills > 0 && personalKills % 50 == 0)
            {
                var data = Epoint.Data.GetPlayerData(killerAccount);
                // 获取这种怪在原版普通模式下的初始血量
                int baseLifeMax = Terraria.ID.ContentSamples.NpcsByNetId[netId].lifeMax;
                // 小怪非线性平滑奖励函数
                int baseMobReward = (int)(200 * (1 - Math.Exp(-baseLifeMax / 250.0)));
                baseMobReward = Math.Clamp(baseMobReward, 50, 150);
                
                int effectiveDays = Math.Max(0, data.TotalDays - 1);
                double daysCoeff = 1.0 + (effectiveDays * 0.05);
                
                int theoreticalReward = baseMobReward;
                if (Epoint.Config.FastPacedMode) theoreticalReward *= 2;

                int baseCap = Epoint.Config.FastPacedMode ? Epoint.Config.BaseDailyCap * 2 : Epoint.Config.BaseDailyCap;
                int dailyCap = (int)(baseCap * daysCoeff);
                int remainingCap = dailyCap - data.PointsToday;
                
                if (remainingCap <= 0) return;

                int actualReward = Math.Min(theoreticalReward, remainingCap);

                Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    if (killerPlayer.Active && actualReward > 0)
                    {
                        Epoint.Data.AddPoints(killerAccount, actualReward);
                        
                        data.PointsToday += actualReward;
                        Epoint.Data.SavePlayerData(data);
                        
                        string capSuffix = (data.PointsToday >= dailyCap) ? " [c/FF0000:(今日积分已达上限！)]" : "";
                        
                        killerPlayer.SendSuccessMessage($"[c/FFD700:达成里程碑！(ง๑ •̀_•́)ง] 击败了 {personalKills} 只 [c/B0E0E6:{npc.FullName}]，奖励 {actualReward} ep{capSuffix}");
                    }
                });
            }
        }
        
        // 提取出来的公共发奖方法：用来给打赢 Boss 的玩家算分
        private static void GrantBossReward(TSPlayer player, string accountName, int baseBossPool, double percent, string bossName)
        {
            TryDailySignIn(player, accountName);
            
            var data = Epoint.Data.GetPlayerData(accountName);
            
            int effectiveDays = Math.Max(0, data.TotalDays - 1);
            double daysCoeff = 1.0 + (effectiveDays * 0.05);

            // 公式：Boss总奖池 * 该玩家输出占比 * 天数加成
            int theoreticalReward = (int)(baseBossPool * percent);
            if (Epoint.Config.FastPacedMode) theoreticalReward *= 2;

            int baseCap = Epoint.Config.FastPacedMode ? Epoint.Config.BaseDailyCap * 2 : Epoint.Config.BaseDailyCap;
            int dailyCap = (int)(baseCap * daysCoeff);
            
            int remainingCap = dailyCap - data.PointsToday;
            if (remainingCap <= 0) return;
            
            int actualReward = Math.Min(theoreticalReward, remainingCap);

            if (actualReward > 0)
            {
                Epoint.Data.AddPoints(accountName, actualReward);
                
                data.PointsToday += actualReward;
                Epoint.Data.SavePlayerData(data);
                
                string capSuffix = (data.PointsToday >= dailyCap) ? " [c/FF0000:(今日积分已达上限！)]" : "";
                
                player.SendSuccessMessage($"[c/FFD700:恭喜(੭ु´ ᐜ `)੭ु⁾⁾] [c/B0E0E6:{bossName}] 已被击败！根据您的表现，奖励 [c/FFD700:{actualReward}] ep{capSuffix}");
            }
        }
    }
}