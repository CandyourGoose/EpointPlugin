using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Timers;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;
using Newtonsoft.Json;

namespace EpointPlugin
{
    // =========================================================================
    // [业务逻辑中心] 负责将所有的计算、指令判断、事件监听
    // =========================================================================
    public static class EpointSystem
    {
        private static readonly Random Rand = new Random(); // 随机数生成器（用于运气波动计算）
        private static System.Timers.Timer? _onlineTimer;   // 负责定时发放在线奖励的时钟
        private static System.Timers.Timer? _autoSignInTimer; // 负责自动发放签到奖励的时钟
        
        private static readonly string[] DyeGradientColors =
        {
            "7FFFD4", "40E0D0", "00FFFF", "66CDAA",
            "FF69B4", "FF1493", "FFD700", "FFA500"
        }; // 颜色带
        // 渐变色彩生成器
        private static string BuildAnimatedGradientText(string text)
        {
            var sb = new System.Text.StringBuilder();
            
            // 在调用的瞬间生成一个随机起始点
            int startOffset = Rand.Next(DyeGradientColors.Length);

            for (int i = 0; i < text.Length; i++)
            {
                // 从数组中取出颜色形成彩虹桥
                string color = DyeGradientColors[(startOffset + i) % DyeGradientColors.Length];
                sb.Append($"[c/{color}:{text[i]}]");
            }

            return sb.ToString();
        }
        
        // 根据 C# 静态只读字段命名规范，去掉下划线并首字母大写
        // 【核心修复】全部升级为工业级线程安全字典
        private static readonly ConcurrentDictionary<string, int> SessionTime = new ConcurrentDictionary<string, int>(); 
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<string, int>> BossDamageTracker = new ConcurrentDictionary<int, ConcurrentDictionary<string, int>>(); 
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, int>> PersonalKillTracker = new ConcurrentDictionary<string, ConcurrentDictionary<int, int>>();

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
        // 独立的掉落物类：记录物品ID、掉落概率、单次掉落数量
        private class BoxDrop
        {
            public int ItemId { get; init; }
            public double Probability { get; init; } // 掉落概率 (0.0 ~ 1.0)
            public int Stack { get; init; } = 1;     // 默认物品只给 1 个
        }

        private class BlindBoxItem
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
            public int IconItemId { get; init; } 
            public string ColorHex { get; init; } = "FFFFFF"; 
            public int DefaultPrice { get; init; } 
            public List<BoxDrop> DropPool { get; init; } = new List<BoxDrop>(); // 定义掉落池
            
            // 通过盲盒 Name 去配置文件里匹配价格
            public int Price => Epoint.Config.BlindBoxPrices.FirstOrDefault(b => b.Name == Name)?.Price ?? DefaultPrice;
        }

        private static readonly List<BlindBoxItem> BlindBoxes = new List<BlindBoxItem>
        {
            new BlindBoxItem { Id = 101, Name = "普通盲盒", IconItemId = 306, ColorHex = "D4AF37", DefaultPrice = 1500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 437, Probability = 1.0 / 6 },
                new BoxDrop { ItemId = 517, Probability = 1.0 / 6 },
                new BoxDrop { ItemId = 535, Probability = 1.0 / 6 },
                new BoxDrop { ItemId = 536, Probability = 1.0 / 6 },
                new BoxDrop { ItemId = 532, Probability = 1.0 / 6 },
                new BoxDrop { ItemId = 554, Probability = 1.0 / 6 }
            } },
            new BlindBoxItem { Id = 102, Name = "冰雪盲盒", IconItemId = 681, ColorHex = "6ECFF6", DefaultPrice = 1500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 1312, Probability = 0.05 },
                new BoxDrop { ItemId = 676, Probability = 0.95 / 3 },
                new BoxDrop { ItemId = 1264, Probability = 0.95 / 3 },
                new BoxDrop { ItemId = 725, Probability = 0.95 / 3 }
            } },
            new BlindBoxItem { Id = 103, Name = "丛林盲盒", IconItemId = 1528, ColorHex = "3A8F3A", DefaultPrice = 2500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 52, Probability = 0.1 },
                new BoxDrop { ItemId = 1724, Probability = 0.1 },
                new BoxDrop { ItemId = 2353, Probability = 0.1, Stack = 10 },
                new BoxDrop { ItemId = 1922, Probability = 0.1 },
                new BoxDrop { ItemId = 678, Probability = 0.1, Stack = 10 },
                new BoxDrop { ItemId = 1336, Probability = 0.1 },
                new BoxDrop { ItemId = 2676, Probability = 0.1, Stack = 5 },
                new BoxDrop { ItemId = 2272, Probability = 0.1 },
                new BoxDrop { ItemId = 5395, Probability = 0.1 },
                new BoxDrop { ItemId = 4986, Probability = 0.1, Stack = 60 }
            } },
            new BlindBoxItem { Id = 104, Name = "腐化盲盒", IconItemId = 1529, ColorHex = "5A3FA0", DefaultPrice = 2500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 3014, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 3008, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 3012, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 3015, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 3023, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 5489, Probability = 0.005 }
            } },
            new BlindBoxItem { Id = 105, Name = "猩红盲盒", IconItemId = 1530, ColorHex = "B03030", DefaultPrice = 2500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 3006, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 3007, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 3009, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 3013, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 3016, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 5489, Probability = 0.005 }
            } },
            new BlindBoxItem { Id = 106, Name = "神圣盲盒", IconItemId = 1531, ColorHex = "F2A6FF", DefaultPrice = 2500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 3029, Probability = 0.995 / 4 },
                new BoxDrop { ItemId = 3030, Probability = 0.995 / 4 },
                new BoxDrop { ItemId = 3051, Probability = 0.995 / 4 },
                new BoxDrop { ItemId = 3022, Probability = 0.995 / 4 },
                new BoxDrop { ItemId = 5488, Probability = 0.005 }
            } },
            new BlindBoxItem { Id = 107, Name = "奇异染料盲盒", IconItemId = 1067, ColorHex = "", DefaultPrice = 1500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 3040, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 3028, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 3560, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 3041, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 3534, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 2872, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 3025, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 3190, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 3553, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 3027, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 3554, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 3555, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 3026, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 2871, Probability = 0.65 / 14 },
                new BoxDrop { ItemId = 2883, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 3561, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 3598, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 3038, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 3597, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 3600, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 2873, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 2869, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 2870, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 2864, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 3556, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 2879, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 3042, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 3533, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 3039, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 2878, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 2885, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 2884, Probability = 0.3 / 18 },
                new BoxDrop { ItemId = 3024, Probability = 0.05 }
            } }
        };
        
        // ================= 模块一：定时器管理 =================
        
        // 初始化在线时长奖励时钟
        public static void InitializeTimer()
        {
            _onlineTimer = new System.Timers.Timer(Epoint.Config.OnlineRewardInterval * 60 * 1000); // 转换时间单位为毫秒
            _onlineTimer.Elapsed += OnOnlineTimerElapsed; // 绑定到点后触发的事件
            _onlineTimer.AutoReset = true; // 允许无限循环跳动
            _onlineTimer.Start();
            
            // 每 60 秒执行一次
            _autoSignInTimer = new System.Timers.Timer(60000);
            _autoSignInTimer.Elapsed += (_, _) =>
            {
                // 自动签到与心跳查岗
                foreach (var player in TShock.Players)
                {
                    if (player is { Active: true, IsLoggedIn: true })
                    {
                        TryDailySignIn(player, player.Account.Name);
                    }
                }
                
                // 定期清理因为自然消失、逃跑而遗留在内存里的幽灵 Boss 数据
                var deadBossKeys = BossDamageTracker.Keys.Where(k => Main.npc[k] == null || !Main.npc[k].active).ToList();
                foreach (var key in deadBossKeys)
                {
                    BossDamageTracker.TryRemove(key, out _);
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
            // 释放所有静态集合，防止重载时内存泄漏
            SessionTime.Clear();
            BossDamageTracker.Clear();
            PersonalKillTracker.Clear();
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
            double daysCoeff = 1.0 + (effectiveDaysForCap * 0.1); // 累计登录系数
            
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
                        msg = $"[c/4CAF50:你感觉今天运气好极了！｡:.ﾟヽ(*´∀`)ﾉﾟ.:｡] 已连续签到 [c/FFD700:{newStreakDays}] [c/FFFFFF:天]，今日奖励 [c/FFD700:{actualReward}] ep";
                    else if (luck < 0.85)
                        msg = $"[c/7B3FBF:似乎今天运气有点差 (☍~⁰。)] 已连续签到 [c/FFD700:{newStreakDays}] [c/FFFFFF:天]，今日奖励 [c/FFD700:{actualReward}] ep";
                    else
                        msg = $"[c/FFD700:签到成功 ～(∠・ω< )⌒★] 已连续签到 [c/FFD700:{newStreakDays}] [c/FFFFFF:天]，今日奖励 [c/FFD700:{actualReward}] ep";

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
                    if (player is { Active: true, IsLoggedIn: true } && player.Account.Name == accountName)
                    {
                        SendMessage();
                    }
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
            args.Player.SendMessage("/eprank [c/FFFFFF:- 查看玩家积分排行榜]", 85, 210, 132);
            args.Player.SendMessage("/epshop [页码][c/AAAAAA:(留空默认取1)][c/FFFFFF: - 打开积分商店 (第 1 页道具商店，第 2 页盲盒商店)]", 85, 210, 132);
            args.Player.SendMessage("/epbuy <商品序号> [购买次数][c/AAAAAA:(留空默认取1)][c/FFFFFF: - 购买商品]", 85, 210, 132);
            args.Player.SendMessage("[c/55CDFF:积分获取途径：][c/FFD700:每日首次登录、累积在线时长、击败Boss奖励、击杀小怪里程碑奖励。]", 85, 210, 132);
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
                
                args.Player.SendInfoMessage("=================== [c/FFD700:Epoint 道具商店] ===================");

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

                args.Player.SendInfoMessage("");
                args.Player.SendInfoMessage("[c/FF7E7E:(注：红色序号为限购商品)]");
                args.Player.SendInfoMessage("==================================================");
                args.Player.SendMessage($"[c/FFFFFF:积分余额：]{currentPoints} ep [c/FFFFFF:|] [c/55CDFF:第 1 页] [c/FFFFFF:(输入] /epshop 2 [c/FFFFFF:查看盲盒商店)]", 85, 210, 132);
            }
            
            else // ============ 第二页：盲盒商店 ============
            {
                args.Player.SendInfoMessage("=================== [c/FFD700:Epoint 盲盒商店] ===================");
                
                // 进度锁提示
                if (!Main.hardMode)
                {
                    args.Player.SendErrorMessage("[c/FFFFFF:【已锁定】] 本页商品需击败 [c/FF7E7E:[血肉墙]] 后方可购买！");
                }
                
                foreach (var box in BlindBoxes)
                {
                    string drops = box.Id == 107 ? "[c/AAAAAA:内含 33 种奇异染料]" : string.Join("", box.DropPool.Select(d => d.Stack > 1 ? $"[i/s{d.Stack}:{d.ItemId}]" : $"[i:{d.ItemId}]"));
                    string boxName = box.Id == 107 ? BuildAnimatedGradientText(box.Name) : $"[c/{box.ColorHex}:{box.Name}]";
                    // 格式: (序号) XX盲盒[图标] XX ep | (可能包含的物品: [图标][图标]...)
                    string line = $"[c/FF7E7E:({box.Id})] {boxName}[i:{box.IconItemId}] [c/00FF00:{box.Price} ep] [c/FFFFFF:| (可能包含的物品: ]{drops}[c/FFFFFF:)]";
                    args.Player.SendMessage(line, 255, 255, 255);
                }
                
                args.Player.SendInfoMessage("==================================================");
                args.Player.SendMessage($"[c/FFFFFF:积分余额：]{currentPoints} ep [c/FFFFFF:|] [c/55CDFF:第 1 页] [c/FFFFFF:(输入] /epshop 1 [c/FFFFFF:返回道具商店)]", 85, 210, 132);
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
                    // ===== 抽卡概率算法 =====
                    double r = Rand.NextDouble(); // 生成 0.0 ~ 1.0 的随机数
                    double cumulative = 0.0;
                    BoxDrop? selectedDrop = null;
                    
                    foreach (var drop in blindBox.DropPool)
                    {
                        cumulative += drop.Probability;
                        if (r <= cumulative)
                        {
                            selectedDrop = drop;
                            break;
                        }
                    }
                    // 如果因为计算机浮点数精度误差没抽到，就给奖池的最后一个物品
                    selectedDrop ??= blindBox.DropPool.Last();

                    // 发放物品
                    args.Player.GiveItem(selectedDrop.ItemId, selectedDrop.Stack);
                    
                    // 广播物品展示标签
                    string dropTag = selectedDrop.Stack > 1 ? $"[i/s{selectedDrop.Stack}:{selectedDrop.ItemId}]" : $"[i:{selectedDrop.ItemId}]";
                    // 拦截染料盲盒，将其广播名字套上流光特效
                    string boxNameDisplay = blindBox.Id == 107 ? BuildAnimatedGradientText(blindBox.Name) : $"[c/{blindBox.ColorHex}:{blindBox.Name}]";
                    // 全服广播开盲盒结果
                    TSPlayer.All.SendMessage($"[c/55CDFF:{accountName}] 开启了 {boxNameDisplay}，获得了 {dropTag}", 255, 255, 255);
                }
            }
        }

        // 积分排行榜模块 (后台异步防卡顿计算)
        public static void EpRankCommand(CommandArgs args)
        {
            Task.Run(() =>
            {
                try
                {
                    string playersDir = Path.Combine(TShock.SavePath, "epoint", "players");
                    if (!Directory.Exists(playersDir)) return;

                    var files = Directory.GetFiles(playersDir, "*.json");
                    var rankList = new List<PlayerAccount>();

                    // 读取所有玩家档案
                    foreach (var file in files)
                    {
                        try
                        {
                            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var reader = new StreamReader(stream);

                            string json = reader.ReadToEnd();
                            var account = JsonConvert.DeserializeObject<PlayerAccount>(json);
                            if (account != null) rankList.Add(account);
                        }
                        catch { /* 忽略损坏的文件 */ }
                    }

                    // 取积分最高的 Top 10
                    var topPlayers = rankList.OrderByDescending(p => p.Points).Take(10).ToList();

                    args.Player.SendMessage("======== Epoint 积分排行榜 ========", 255, 215, 0);
                    
                    if (topPlayers.Count == 0)
                    {
                        args.Player.SendInfoMessage("暂无玩家数据");
                        return;
                    }

                    for (int i = 0; i < topPlayers.Count; i++)
                    {
                        string rankColor = i switch
                        {
                            0 => "FFD700",
                            1 => "C0C0C0",
                            2 => "CD7F32",
                            _ => "FFFFFF"
                        };
                        args.Player.SendMessage($"[c/{rankColor}:Top {i + 1}.] {topPlayers[i].PlayerName} - [c/00FF00:{topPlayers[i].Points} ep]", 255, 255, 255);
                    }
                }
                catch (Exception ex)
                {
                    args.Player.SendErrorMessage($"[Epoint] 排行榜读取失败: {ex.Message}");
                }
            });
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
        
        // 累计在线时长定时器逻辑
        private static void OnOnlineTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // 获取当前真实存活的玩家账号哈希表，用于校验
            var activeAccounts = TShock.Players
                .Where(p => p is { Active: true, IsLoggedIn: true })
                .Select(p => p.Account.Name)
                .ToHashSet();

            // 清理非正常掉线的幽灵数据
            foreach (var account in SessionTime.Keys)
            {
                if (!activeAccounts.Contains(account)) SessionTime.TryRemove(account, out _);
            }

            // 遍历服务器里的所有玩家
            foreach (var player in TShock.Players)
            {
                if (player == null || !player.Active || !player.IsLoggedIn) continue;

                string accountName = player.Account.Name;
                TryDailySignIn(player, accountName);
                
                // 修改并发安全，原子化累加在线时长
                SessionTime.AddOrUpdate(accountName, Epoint.Config.OnlineRewardInterval, (_, v) => v + Epoint.Config.OnlineRewardInterval);

                var data = Epoint.Data.GetPlayerData(accountName);
                int effectiveDays = Math.Max(0, data.TotalDays - 1);
                double daysCoeff = 1.0 + (effectiveDays * 0.05);

                // ================= 在线奖励暴击算法 =================
                double randLucky = Rand.NextDouble();
                int baseOnlineReward;
                string msgPrefix;

                if (randLucky < 0.04) // 4% 概率：超级暴击
                {
                    baseOnlineReward = 50;
                    msgPrefix = "[c/87CEEB:在线奖励超级暴击！！]";
                }
                else if (randLucky < 0.14) // 10% 概率：普通暴击
                {
                    baseOnlineReward = 30;
                    msgPrefix = "[c/87CEEB:在线奖励暴击！]";
                }
                else // 86% 概率：正常奖励
                {
                    baseOnlineReward = 10;
                    msgPrefix = "[c/87CEEB:在线奖励]";
                }

                // 快节奏模式双倍加成
                int theoreticalReward = Epoint.Config.FastPacedMode ? baseOnlineReward * 2 : baseOnlineReward;

                // ================= 积分拦截与发放 =================
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
                    
                    player.SendSuccessMessage($"[c/55CDFF:叮咚～(∠・ω< )⌒★] {msgPrefix} [c/FFD700:{actualReward}] ep{capSuffix}");
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
                SessionTime.TryRemove(player.Account.Name, out _);
                PersonalKillTracker.TryRemove(player.Account.Name, out _);
            }
        }
        
        // 服务器里如果有 NPC 被打则会触发这个钩子
        public static void OnNpcStrike(object? sender, GetDataHandlers.NPCStrikeEventArgs args)
        {
            var player = args.Player;
            var npc = Main.npc[args.ID];

            if (!player.Active || !player.IsLoggedIn) return;
            if (!npc.active) return;
            
            bool isEow = npc.netID is 13 or 14 or 15; // 识别世界吞噬怪的体节 (13=头, 14=身, 15=尾)
            if (!npc.boss && !MiniBossIds.Contains(npc.netID) && !isEow) return; // 忽略既不是原版 Boss 也不在特殊小头目白名单里的敌怪
            
            string accountName = player.Account.Name;
            
            // 在记录伤害时，多部件 Boss 统一映射到其核心本体 ID 上
            int bossKey;
            if (isEow) 
            {
                bossKey = -13; // 世吞单独处理：将所有世吞体节虚拟统合在 ID-13 上
            }
            else 
            {
                bossKey = npc.realLife >= 0 ? npc.realLife : npc.whoAmI;
            }
            
            // 确保内部字典存在，并原子化累加伤害
            var bossDict = BossDamageTracker.GetOrAdd(bossKey, _ => new ConcurrentDictionary<string, int>());
            bossDict.AddOrUpdate(accountName, args.Damage, (_, v) => v + args.Damage);
        }

        // 服务器里如果有怪物被击杀则会触发这个钩子
        public static void OnNpcKilled(NpcKilledEventArgs args)
        {
            var npc = args.npc;
            int netId = npc.netID;
            bool isEow = netId is 13 or 14 or 15;

            // ---- 情况A：击杀 Boss 或 特殊小头目 ----
            if (npc.boss || MiniBossIds.Contains(netId) || isEow)
            {
                // 在 Boss 结算的最开始遍历一次当前所有合法的在线玩家存入缓存列表
                var activePlayers = TShock.Players.Where(p => p is { Active: true, IsLoggedIn: true, Account: not null }).ToList();
                
                int bossKey;
                int baseBossPool;
                string bossName;

                if (isEow)
                {
                    // 世吞单独处理：检查全图是否还有其他存活的世吞体节，条件 n.whoAmI != npc.whoAmI 排除掉当前正在死亡的这块体节
                    bool eowAlive = Main.npc.Any(n => n is { active: true, netID: 13 or 14 or 15 } && n.whoAmI != npc.whoAmI);
                    if (eowAlive) return;

                    bossKey = -13;
                    bossName = "世界吞噬怪";
                    
                    // 单独计算世吞血量上限
                    int playerCount = activePlayers.Count;
                    double dynamicLifeMax = 10000 * (1.0 + Math.Max(0, playerCount - 1) * 0.35);
                    
                    baseBossPool = (int)(400 * (1 - Math.Exp(-dynamicLifeMax / 15000.0))); // Boss 非线性平滑奖励函数
                }
                else
                {
                    bossKey = npc.realLife >= 0 ? npc.realLife : npc.whoAmI;
                    bossName = npc.FullName;
                    baseBossPool = (int)(400 * (1 - Math.Exp(-npc.lifeMax / 15000.0))); // Boss 非线性平滑奖励函数
                }
                
                baseBossPool = Math.Clamp(baseBossPool, 100, 400); // 调整 Boss 奖励上下限
                
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
                            
                            var p = activePlayers.FirstOrDefault(pl => pl.Account.Name == accountName);
                            if (p != null) GrantBossReward(p, accountName, baseBossPool, damagePercent, bossName);
                        }
                    }
                }
                else
                {
                    // 模式2：只要在线的登录玩家，全部平分奖励积分池
                    if (activePlayers.Count > 0)
                    {
                        double equalPercent = 1.0 / activePlayers.Count;
                        foreach (var p in activePlayers) GrantBossReward(p, p.Account.Name, baseBossPool, equalPercent, bossName);
                    }
                }
                
                BossDamageTracker.TryRemove(bossKey, out _); // 积分分配完成，把这只 Boss 的伤害数据从内存删掉
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

            // 在玩家的个人字典里原子化累加击杀数
            var killDict = PersonalKillTracker.GetOrAdd(killerAccount, _ => new ConcurrentDictionary<int, int>());
            int personalKills = killDict.AddOrUpdate(netId, 1, (_, v) => v + 1);
            
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
                    await Task.Delay(2000);
                    if (killerPlayer is { Active: true, IsLoggedIn: true } && killerPlayer.Account.Name == killerAccount && actualReward > 0)
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