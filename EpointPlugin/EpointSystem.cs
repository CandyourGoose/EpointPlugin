using System;
using System.IO;
using System.Linq;
using System.Timers;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Terraria;
using TerrariaApi.Server;
using On.Terraria.GameContent; 
using TShockAPI;
using TShockAPI.Hooks;
using Newtonsoft.Json;

namespace EpointPlugin
{
    /// <summary>
    /// [积分系统核心模块]
    /// 管理在线奖励、签到、商店购买、盲盒抽取、Boss/小怪击杀奖励等积分获取逻辑
    /// 数据持久化通过 Epoint.Data 完成，定时器和事件钩子在此类中初始化
    /// </summary>
    public static class EpointSystem
    {
        // 随机数生成器，用于签到运气、积分暴击、盲盒抽取等概率判定
        private static readonly Random Rand = new Random(); 
        // 在线奖励计时器，按配置间隔触发
        private static System.Timers.Timer? _onlineTimer;   
        // 自动签到及全局数据刷写计时器，每分钟执行一次
        private static System.Timers.Timer? _autoSignInTimer; 
        
        // 动态渐变文本的颜色列表，用于盲盒名称等特效显示
        private static readonly string[] DyeGradientColors =
        {
            "7FFFD4", "40E0D0", "00FFFF", "66CDAA", "FF69B4", "FF1493", "FFD700", "FFA500"
        }; 
        
        /// <summary>
        /// 生成带颜色渐变效果的文本，每个字符独立取色，起始偏移随机
        /// </summary>
        private static string BuildAnimatedGradientText(string text)
        {
            var sb = new System.Text.StringBuilder();
            int startOffset = Rand.Next(DyeGradientColors.Length);
            for (int i = 0; i < text.Length; i++)
            {
                string color = DyeGradientColors[(startOffset + i) % DyeGradientColors.Length];
                sb.Append($"[c/{color}:{text[i]}]");
            }
            return sb.ToString();
        }
        
        // 玩家在线时长累计，用于发放在线奖励
        private static readonly ConcurrentDictionary<string, int> SessionTime = new(); 
        // 个人怪物击杀统计：key=玩家名，value=怪物Id -> 累计击杀数
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, int>> PersonalKillTracker = new();
        // Boss实体存活跟踪：key=Boss唯一标识，value=参与玩家数
        private static readonly ConcurrentDictionary<int, int> BossSpawnTracker = new(); 
        // Boss伤害追踪：key=Boss唯一标识，value=玩家名 -> 总伤害
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<string, int>> CustomDamageTracker = new();
        // 折扣保底机制：记录每个玩家对每个非限购商品连续未触发折扣的次数
        private static readonly ConcurrentDictionary<string, Dictionary<int, int>> DiscountMisses = new();
        // 当前生效的折扣标记：玩家 -> 商品id -> 是否享受折扣
        private static readonly ConcurrentDictionary<string, Dictionary<int, bool>> ActiveDiscounts = new();
        // 灵韵积分获取保底计数器：玩家名 -> 连续未获得灵韵积分的次数
        private static readonly ConcurrentDictionary<string, int> CpMisses = new();
        // Boss基础倍率映射表：BossId -> 基础奖励倍率
        private static readonly Dictionary<int, double> BossBasePoolMultipliers = new()
        {
            { 50, 0.1 }, 
            { 4, 0.15 }, 
            { 13, 0.07 }, 
            { 14, 0.07 }, 
            { 15, 0.07 }, 
            { 266, 0.2 }, 
            { 222, 0.22 }, 
            { 668, 0.22 }, 
            { 35, 0.25 }, 
            { 113, 0.3 }, 
            { 657, 0.55 }, 
            { 125, 0.3 }, 
            { 126, 0.3 }, 
            { 134, 0.65 }, 
            { 127, 0.7 }, 
            { 262, 0.75 }, 
            { 245, 0.8 }, 
            { 370, 0.85 }, 
            { 636, 0.9 }, 
            { 439, 0.8 }, 
            { 398, 1.0 }, 
            { 491, 0.1 }, 
            { 315, 0.2 }, 
            { 345, 0.3 }, 
            { 392, 0.4 }, 
            { 493, 0.5 }, 
            { 507, 0.5 }, 
            { 422, 0.5 }, 
            { 517, 0.5 }
        };

        /// <summary> 根据Boss netId获取基础奖励倍率，未配置则返回默认0.1 </summary>
        private static double GetBossBaseMultiplier(int netId) => BossBasePoolMultipliers.GetValueOrDefault(netId, 0.1);
        
        // ================= 条件初始化 =================
        
        /// <summary>
        /// 根据玩家累计消费和全物品收集情况计算会员等级及折扣。
        /// 返回值：会员名、颜色十六进制、折扣系数（1.0表示无折扣）
        /// </summary>
        private static (string Name, string Color, double Discount) GetMembership(PlayerAccount data)
        {
            lock (data.SyncLock)
            {
                bool hasAllItems = Epoint.Config.ShopItems.Count > 0 && 
                                   Epoint.Config.ShopItems.All(item => data.PurchasedNormalItems.Contains(item.Id));

                if (data.TotalSpent >= 60.0 * Epoint.Config.BaseDailyCap && hasAllItems) return ("至尊会员", "A32CC4", 0.60); 
                if (data.TotalSpent >= 25.0 * Epoint.Config.BaseDailyCap) return ("高级会员", "FFD700", 0.85); 
                if (data.TotalSpent >= 15.0 * Epoint.Config.BaseDailyCap) return ("中级会员", "C0C0C0", 0.90); 
                if (data.TotalSpent >= 7.5 * Epoint.Config.BaseDailyCap) return ("初级会员", "CD7F32", 0.95); 
            }
            return ("", "", 1.0);
        }

        // ================= 盲盒配置 =================
        /// <summary> 盲盒掉落物品定义 </summary>
        private class BoxDrop
        {
            public int ItemId { get; init; }
            public double Probability { get; init; }   // 掉落概率，总和应为 1
            public int Stack { get; init; } = 1;     
        }

        /// <summary> 盲盒商品定义 </summary>
        private class BlindBoxItem
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
            public int IconItemId { get; init; }
            public string ColorHex { get; init; } = "FFFFFF"; 
            public int DefaultPrice { get; init; } 
            public List<BoxDrop> DropPool { get; init; } = new List<BoxDrop>(); 
            
            /// <summary> 实际价格：优先读取配置文件中的覆盖价格，否则使用默认 </summary>
            public int Price => Epoint.Config.BlindBoxPrices.FirstOrDefault(b => b.Name == Name)?.Price ?? DefaultPrice;
        }

        // 预置盲盒列表，包含ID、名称、图标、默认价格及掉落池
        private static readonly List<BlindBoxItem> BlindBoxes = new List<BlindBoxItem>
        {
            new BlindBoxItem { Id = 201, Name = "普通盲盒", IconItemId = 306, ColorHex = "D4AF37", DefaultPrice = 1500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 437, Probability = 1.0 / 6 }, new BoxDrop { ItemId = 517, Probability = 1.0 / 6 },
                new BoxDrop { ItemId = 535, Probability = 1.0 / 6 }, new BoxDrop { ItemId = 536, Probability = 1.0 / 6 },
                new BoxDrop { ItemId = 532, Probability = 1.0 / 6 }, new BoxDrop { ItemId = 554, Probability = 1.0 / 6 }
            } },
            new BlindBoxItem { Id = 202, Name = "冰雪盲盒", IconItemId = 681, ColorHex = "6ECFF6", DefaultPrice = 1500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 1312, Probability = 0.05 }, new BoxDrop { ItemId = 676, Probability = 0.95 / 3 },
                new BoxDrop { ItemId = 1264, Probability = 0.95 / 3 }, new BoxDrop { ItemId = 725, Probability = 0.95 / 3 }
            } },
            new BlindBoxItem { Id = 203, Name = "丛林盲盒", IconItemId = 1528, ColorHex = "3A8F3A", DefaultPrice = 2500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 52, Probability = 0.1 }, new BoxDrop { ItemId = 1724, Probability = 0.1 },
                new BoxDrop { ItemId = 2353, Probability = 0.1, Stack = 10 }, new BoxDrop { ItemId = 1922, Probability = 0.1 },
                new BoxDrop { ItemId = 678, Probability = 0.1, Stack = 10 }, new BoxDrop { ItemId = 1336, Probability = 0.1 },
                new BoxDrop { ItemId = 2676, Probability = 0.1, Stack = 5 }, new BoxDrop { ItemId = 2272, Probability = 0.1 },
                new BoxDrop { ItemId = 5395, Probability = 0.1 }, new BoxDrop { ItemId = 4986, Probability = 0.1, Stack = 60 }
            } },
            new BlindBoxItem { Id = 204, Name = "腐化盲盒", IconItemId = 1529, ColorHex = "5A3FA0", DefaultPrice = 2500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 3014, Probability = 0.995 / 5 }, new BoxDrop { ItemId = 3008, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 3012, Probability = 0.995 / 5 }, new BoxDrop { ItemId = 3015, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 3023, Probability = 0.995 / 5 }, new BoxDrop { ItemId = 5489, Probability = 0.005 }
            } },
            new BlindBoxItem { Id = 205, Name = "猩红盲盒", IconItemId = 1530, ColorHex = "B03030", DefaultPrice = 2500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 3006, Probability = 0.995 / 5 }, new BoxDrop { ItemId = 3007, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 3009, Probability = 0.995 / 5 }, new BoxDrop { ItemId = 3013, Probability = 0.995 / 5 },
                new BoxDrop { ItemId = 3016, Probability = 0.995 / 5 }, new BoxDrop { ItemId = 5489, Probability = 0.005 }
            } },
            new BlindBoxItem { Id = 206, Name = "神圣盲盒", IconItemId = 1531, ColorHex = "F2A6FF", DefaultPrice = 2500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 3029, Probability = 0.995 / 4 }, new BoxDrop { ItemId = 3030, Probability = 0.995 / 4 },
                new BoxDrop { ItemId = 3051, Probability = 0.995 / 4 }, new BoxDrop { ItemId = 3022, Probability = 0.995 / 4 },
                new BoxDrop { ItemId = 5488, Probability = 0.005 }
            } },
            new BlindBoxItem { Id = 207, Name = "奇异染料盲盒", IconItemId = 1067, ColorHex = "", DefaultPrice = 1500, DropPool = new List<BoxDrop> {
                new BoxDrop { ItemId = 3040, Probability = 0.532 / 14 }, new BoxDrop { ItemId = 3028, Probability = 0.532 / 14 },
                new BoxDrop { ItemId = 3560, Probability = 0.532 / 14 }, new BoxDrop { ItemId = 3041, Probability = 0.532 / 14 },
                new BoxDrop { ItemId = 3534, Probability = 0.532 / 14 }, new BoxDrop { ItemId = 2872, Probability = 0.532 / 14 },
                new BoxDrop { ItemId = 3025, Probability = 0.532 / 14 }, new BoxDrop { ItemId = 3190, Probability = 0.532 / 14 },
                new BoxDrop { ItemId = 3553, Probability = 0.532 / 14 }, new BoxDrop { ItemId = 3027, Probability = 0.532 / 14 },
                new BoxDrop { ItemId = 3554, Probability = 0.532 / 14 }, new BoxDrop { ItemId = 3555, Probability = 0.532 / 14 },
                new BoxDrop { ItemId = 3026, Probability = 0.532 / 14 }, new BoxDrop { ItemId = 2871, Probability = 0.532 / 14 },
                new BoxDrop { ItemId = 2883, Probability = 0.45 / 18 }, new BoxDrop { ItemId = 3561, Probability = 0.45 / 18 },
                new BoxDrop { ItemId = 3598, Probability = 0.45 / 18 }, new BoxDrop { ItemId = 3038, Probability = 0.45 / 18 },
                new BoxDrop { ItemId = 3597, Probability = 0.45 / 18 }, new BoxDrop { ItemId = 3600, Probability = 0.45 / 18 },
                new BoxDrop { ItemId = 2873, Probability = 0.45 / 18 }, new BoxDrop { ItemId = 2869, Probability = 0.45 / 18 },
                new BoxDrop { ItemId = 2870, Probability = 0.45 / 18 }, new BoxDrop { ItemId = 2864, Probability = 0.45 / 18 },
                new BoxDrop { ItemId = 3556, Probability = 0.45 / 18 }, new BoxDrop { ItemId = 2879, Probability = 0.45 / 18 },
                new BoxDrop { ItemId = 3042, Probability = 0.45 / 18 }, new BoxDrop { ItemId = 3533, Probability = 0.45 / 18 },
                new BoxDrop { ItemId = 3039, Probability = 0.45 / 18 }, new BoxDrop { ItemId = 2878, Probability = 0.45 / 18 },
                new BoxDrop { ItemId = 2885, Probability = 0.45 / 18 }, new BoxDrop { ItemId = 2884, Probability = 0.45 / 18 },
                new BoxDrop { ItemId = 3024, Probability = 0.018 }
            } }
        };
        
        // ================= 定时器与底层钩子管理 =================
        
        /// <summary>
        /// 初始化定时器：在线奖励定时器、自动签到/数据刷写定时器，并挂载Boss击杀钩子
        /// 插件启用时调用
        /// </summary>
        public static void InitializeTimer()
        {
            _onlineTimer = new System.Timers.Timer(Epoint.Config.OnlineRewardInterval * 60 * 1000); 
            _onlineTimer.Elapsed += OnOnlineTimerElapsed; 
            _onlineTimer.AutoReset = true; 
            _onlineTimer.Start();
            
            _autoSignInTimer = new System.Timers.Timer(60000);
            _autoSignInTimer.Elapsed += (_, _) =>
            {
                foreach (var player in TShock.Players)
                {
                    if (player is { Active: true, IsLoggedIn: true })
                        TryDailySignIn(player, player.Account.Name);
                }
                
                // 清理已死亡的Boss追踪数据
                var deadBossKeys = BossSpawnTracker.Keys.Where(k => Main.npc[k] == null || !Main.npc[k].active).ToList();
                foreach (var key in deadBossKeys) 
                {
                    BossSpawnTracker.TryRemove(key, out _);
                    CustomDamageTracker.TryRemove(key, out _); 
                }
                
                Epoint.Data.FlushAll();
            };
            _autoSignInTimer.AutoReset = true;
            _autoSignInTimer.Start();

            BossDamageTracker.OnBossKilled += NativeBossDamageTracker_OnBossKilled;
        }

        /// <summary> 释放定时器资源，清理所有缓存字典，解绑事件钩子 </summary>
        public static void Dispose()
        {
            _onlineTimer?.Stop();
            _onlineTimer?.Dispose();
            _autoSignInTimer?.Stop();
            _autoSignInTimer?.Dispose();
            SessionTime.Clear();
            BossSpawnTracker.Clear();
            PersonalKillTracker.Clear();
            CustomDamageTracker.Clear();
            DiscountMisses.Clear();
            ActiveDiscounts.Clear();
            CpMisses.Clear();
            
            BossDamageTracker.OnBossKilled -= NativeBossDamageTracker_OnBossKilled;
        }
        
        /// <summary> 热重载时更新在线奖励计时器间隔 </summary>
        public static void ReloadTimer()
        {
            if (_onlineTimer != null) _onlineTimer.Interval = Epoint.Config.OnlineRewardInterval * 60 * 1000;
        }

        // ================= 自动签到模块 =================
        /// <summary>
        /// 尝试执行每日签到，使用 lock 保证并发安全。
        /// </summary>
        /// <param name="player">触发签到的玩家对象</param>
        /// <param name="accountName">玩家账号名称</param>
        /// <param name="delayMessage">是否延迟发送消息，用于登录时避免消息过早被吞</param>
        private static void TryDailySignIn(TSPlayer player, string accountName, bool delayMessage = false)
        {
            var data = Epoint.Data.GetPlayerData(accountName);

            string todayStr = DateTime.Now.ToString("yyyy-MM-dd");
            int newStreakDays, actualReward, theoreticalReward;
            double luck;
            
            lock (data.SyncLock)
            {
                if (data.LastLoginDate == todayStr) return;

                int newTotalDays = data.TotalDays + 1;
                newStreakDays = 1;

                if (!string.IsNullOrEmpty(data.LastLoginDate) && DateTime.TryParse(data.LastLoginDate, out DateTime lastDate))
                {
                    if ((DateTime.Now.Date - lastDate.Date).Days == 1) newStreakDays = data.StreakDays + 1;
                }

                int baseCap = Epoint.Config.FastPacedMode ? Epoint.Config.BaseDailyCap * 2 : Epoint.Config.BaseDailyCap; 
                int dailyCap = (int)(0.9 * baseCap + 0.1 * baseCap * data.TotalDays); 
                
                double streakCoeff = Math.Min(1.0 + (newStreakDays * 0.01), 1.15); 
                
                // 正态分布随机运气值，范围钳制在[0.5,1.5]
                double u1 = 1.0 - Rand.NextDouble();
                double u2 = 1.0 - Rand.NextDouble();
                double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
                luck = Math.Clamp(1.0 + 0.08 * randStdNormal, 0.5, 1.5); 

                theoreticalReward = (int)((0.49 * baseCap + 0.01 * baseCap * data.TotalDays) * streakCoeff * luck);
                actualReward = Math.Min(theoreticalReward, dailyCap); 

                data.Points += actualReward;
                data.TotalEarned += actualReward; 
                data.PointsToday = actualReward; 
                data.TotalDays = newTotalDays;
                data.StreakDays = newStreakDays;
                data.LastLoginDate = todayStr;
                data.VipLevel = GetMembership(data).Name; 
            }
            Epoint.Data.SavePlayerData(data);

            void SendMessage()
            {
                if (player.Active)
                {
                    string msg;
                    if (luck > 1.15)
                        msg = $"[c/4CAF50:你感觉今天运气好极了！｡:.ﾟヽ(*´∀`)ﾉﾟ.:｡] 已连续签到 [c/FFD700:{newStreakDays}] [c/FFFFFF:天]，今日奖励 [c/FFD700:{actualReward}] ep";
                    else if (luck < 0.85)
                        msg = $"[c/7B3FBF:今天运气似乎有点差 (☍~⁰。)] 已连续签到 [c/FFD700:{newStreakDays}] [c/FFFFFF:天]，今日奖励 [c/FFD700:{actualReward}] ep";
                    else
                        msg = $"[c/FFD700:签到成功 ～(∠・ω< )⌒★] 已连续签到 [c/FFD700:{newStreakDays}] [c/FFFFFF:天]，今日奖励 [c/FFD700:{actualReward}] ep";

                    player.SendMessage(msg, 255, 255, 255);

                    if (actualReward < theoreticalReward) 
                        player.SendErrorMessage("今日获取积分已达上限！"); 
                }
            }

            if (delayMessage)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    if (player is { Active: true, IsLoggedIn: true } && player.Account.Name == accountName)
                        SendMessage();
                });
            }
            else SendMessage();
        }

        // ================= 插件指令处理 =================
        
        /// <summary> 指令 /ephelp：显示所有积分相关指令帮助 </summary>
        public static void EpHelpCommand(CommandArgs args)
        {
            args.Player.SendInfoMessage("==================== [c/FFD700:Epoint 积分插件帮助菜单] ====================");
            args.Player.SendMessage("/epinfo [c/FFFFFF:- 查看个人积分档案与签到状态]", 85, 210, 132);
            args.Player.SendMessage("/eprank [c/FFFFFF:- 查看玩家积分排行榜]", 85, 210, 132);
            args.Player.SendMessage("/epshop [页码][c/AAAAAA:(留空默认取1)][c/FFFFFF: - 打开积分商店]", 85, 210, 132);
            args.Player.SendMessage("/epbuy <商品序号> [购买次数][c/AAAAAA:(留空默认取1)][c/FFFFFF: - 购买商品]", 85, 210, 132);
            args.Player.SendMessage("[c/55CDFF:积分获取途径：][c/FFD700:每日首次登录、在线时长、击败Boss、小怪里程碑。]", 85, 210, 132);
        }

        /// <summary> 计算字符串在控制台显示时的视觉宽度 </summary>
        private static int GetDisplayWidth(string str) => str.Sum(c => c > 255 ? 2 : 1);

        /// <summary> 指令 /epinfo：显示玩家积分档案，包括余额、会员等级、签到记录等。并自动尝试签到。 </summary>
        public static void EpInfoCommand(CommandArgs args)
        {
            if (!args.Player.Active || !args.Player.IsLoggedIn)
            {
                args.Player.SendErrorMessage("[Epoint] 请先登录");
                return;
            }

            string accountName = args.Player.Account.Name;
            TryDailySignIn(args.Player, accountName); 
            
            var data = Epoint.Data.GetPlayerData(accountName);

            int dailyCap;
            string vipName, vipColor;
            int points, charmPoints, pointsToday, totalDays, streakDays;
            long totalEarned, totalSpent;

            lock (data.SyncLock)
            {
                int baseCap = Epoint.Config.FastPacedMode ? Epoint.Config.BaseDailyCap * 2 : Epoint.Config.BaseDailyCap;
                dailyCap = (int)(0.9 * baseCap + 0.1 * baseCap * data.TotalDays);
                var vip = GetMembership(data);
                vipName = vip.Name;
                vipColor = vip.Color;
                points = data.Points;
                charmPoints = data.CharmPoints;
                pointsToday = data.PointsToday;
                totalDays = data.TotalDays;
                streakDays = data.StreakDays;
                totalEarned = data.TotalEarned;
                totalSpent = data.TotalSpent;
            }

            args.Player.SendInfoMessage($"======== [c/87CEEB:{accountName} 的积分档案] ========");
            if (!string.IsNullOrEmpty(vipName))
                args.Player.SendMessage($"[c/FFD700:会员等级:] [c/{vipColor}:{vipName}]", 255, 255, 255);
            args.Player.SendSuccessMessage($"[c/FFD700:积分余额:] {points} ep");
            args.Player.SendMessage($"[c/A32CC4:灵韵积分:] {charmPoints} cp", 255, 255, 255);
            args.Player.SendSuccessMessage($"[c/FFA500:今日获取:] {pointsToday} / {dailyCap} ep");
            args.Player.SendInfoMessage($"[c/55CDFF:累积登录:] {totalDays} [c/FFFFFF:天]");
            args.Player.SendInfoMessage($"[c/87CEEB:连续签到:] {streakDays} [c/FFFFFF:天]");
            
            args.Player.SendMessage($"[c/A9A9A9:已累计获取积分:] {totalEarned} ep", 255, 255, 255);
            args.Player.SendMessage($"[c/A9A9A9:已累计消费积分:] {totalSpent} ep", 255, 255, 255);
        }

        /// <summary>
        /// 指令 /epshop [页码]：显示商店页面
        /// 页码1：普通道具商店（消耗ep）
        /// 页码2：盲盒商店（消耗ep，需击败肉山解锁）
        /// 页码3：神秘商店（消耗cp，需首次累计10cp解锁）
        /// </summary>
        public static void EpShopCommand(CommandArgs args)
        {
            if (!args.Player.Active || !args.Player.IsLoggedIn)
            {
                args.Player.SendErrorMessage("[Epoint] 请先登录");
                return;
            }
            
            TryDailySignIn(args.Player, args.Player.Account.Name); 

            int page = 1; 
            if (args.Parameters.Count > 0 && (!int.TryParse(args.Parameters[0], out page) || page < 1 || page > 3))
            {
                args.Player.SendErrorMessage("无效的页码。");
                return;
            }
            
            var data = Epoint.Data.GetPlayerData(args.Player.Account.Name);
            bool isMysticUnlocked;
            int currentPoints, charmPoints;
            string vipPrefix;
            double vipMult;

            lock (data.SyncLock)
            {
                isMysticUnlocked = data.IsMysticShopUnlocked;
                currentPoints = data.Points;
                charmPoints = data.CharmPoints;
                var vip = GetMembership(data);
                vipMult = vip.Discount;
                vipPrefix = string.IsNullOrEmpty(vip.Name) ? "" : $"[c/{vip.Color}:【{vip.Name}】] ";
            }

            if (page == 3 && !isMysticUnlocked)
            {
                args.Player.SendErrorMessage("前面的区域，以后再来探索吧~"); 
                return;
            }

            var myActiveDiscounts = ActiveDiscounts.GetOrAdd(args.Player.Account.Name, _ => new Dictionary<int, bool>());

            if (page == 1) 
            {
                var items = Epoint.Config.ShopItems;
                if (items.Count == 0) return;
                
                args.Player.SendInfoMessage("=================== [c/FFD700:Epoint 道具商店] ===================");

                for (int i = 0; i < items.Count; i += 3) 
                {
                    string line = "";
                    for (int j = 0; j < 3 && i + j < items.Count; j++) 
                    {
                        var item = items[i + j];
                        string itemTag = item.Stack > 1 ? $"[i/s{item.Stack}:{item.ItemNetId}]" : $"[i:{item.ItemNetId}]";
                        string idColor = item.IsOneTime ? "FF7E7E" : "FFFFFF";
                        
                        bool hasPity = !item.IsOneTime && myActiveDiscounts.GetValueOrDefault(item.Id, false);
                        double finalMult = hasPity ? vipMult * 0.70 : vipMult;
                        int finalPrice = (int)(item.Price * finalMult);
                        
                        string priceDisplayTag;
                        string rawDisplayStr;
                        
                        if (finalPrice < item.Price)
                        {
                            string highlightColor = hasPity ? "FF69B4" : "FFD700"; 
                            priceDisplayTag = $"[c/{highlightColor}:{finalPrice} ep]";
                            rawDisplayStr = $"({item.Id}) {finalPrice} ep";
                        }
                        else
                        {
                            priceDisplayTag = $"[c/00FF00:{item.Price} ep]";
                            rawDisplayStr = $"({item.Id}) {item.Price} ep";
                        }
                        
                        int paddingSpaces = Math.Max(2, 22 - GetDisplayWidth(rawDisplayStr) - 3); 
                        string itemStr = $"[c/{idColor}:({item.Id})] {itemTag} {priceDisplayTag}" + new string(' ', paddingSpaces);
                        line += itemStr;
                    }
                    args.Player.SendMessage(line, 255, 255, 255); 
                }

                args.Player.SendInfoMessage("");
                args.Player.SendInfoMessage("[c/FF7E7E:(注：红色序号为限购商品)]");
                args.Player.SendInfoMessage("==================================================");
                args.Player.SendMessage($"{vipPrefix}[c/FFFFFF:积分余额：]{currentPoints} ep [c/FFFFFF:|] [c/55CDFF:第 1 页] [c/FFFFFF:(输入] /epshop 2 [c/FFFFFF:查看盲盒商店)]", 85, 210, 132);
            }
            else if (page == 2)
            {
                args.Player.SendInfoMessage("=================== [c/FFD700:Epoint 盲盒商店] ===================");
                if (!Main.hardMode) args.Player.SendErrorMessage("[c/FFFFFF:【已锁定】] 本页商品需击败 [c/FF7E7E:[血肉墙]] 后方可购买！");
                
                foreach (var box in BlindBoxes)
                {
                    string drops = box.Id == 107 ? "[c/AAAAAA:33 种奇异染料]" : string.Join("", box.DropPool.Select(d => d.Stack > 1 ? $"[i/s{d.Stack}:{d.ItemId}]" : $"[i:{d.ItemId}]"));
                    string boxName = box.Id == 107 ? BuildAnimatedGradientText(box.Name) : $"[c/{box.ColorHex}:{box.Name}]";
                    
                    int basePrice = box.Price;
                    int finalPrice = (int)(basePrice * vipMult);
                    string priceTag = finalPrice < basePrice ? $"[c/FFD700:{finalPrice} ep]" : $"[c/00FF00:{basePrice} ep]";

                    string line = $"[c/FF7E7E:({box.Id})] {boxName}[i:{box.IconItemId}] {priceTag} [c/FFFFFF:| (可能包含的物品: ]{drops}[c/FFFFFF:)]";
                    args.Player.SendMessage(line, 255, 255, 255);
                }
                
                args.Player.SendInfoMessage("==================================================");
                string nextStr = isMysticUnlocked ? "[c/FFFFFF:(输入] /epshop 3 [c/FFFFFF:查看神秘商店)]" : "[c/FFFFFF:(输入] /epshop 1 [c/FFFFFF:返回道具商店)]";
                args.Player.SendMessage($"{vipPrefix}[c/FFFFFF:积分余额：]{currentPoints} ep [c/FFFFFF:|] [c/55CDFF:第 2 页] {nextStr}", 85, 210, 132);
            }
            else
            {
                var items = Epoint.Config.MysticShopItems;
                if (items.Count == 0) return;
                
                args.Player.SendInfoMessage("=================== [c/A32CC4:Epoint 神秘商店] ===================");

                for (int i = 0; i < items.Count; i += 3) 
                {
                    string line = "";
                    for (int j = 0; j < 3 && i + j < items.Count; j++) 
                    {
                        var item = items[i + j];
                        string itemTag = item.Stack > 1 ? $"[i/s{item.Stack}:{item.ItemNetId}]" : $"[i:{item.ItemNetId}]";
                        string idColor = item.IsOneTime ? "FF7E7E" : "FFFFFF";
                        
                        string priceDisplayTag = $"[c/A32CC4:{item.Price} cp]";
                        string rawDisplayStr = $"({item.Id}) {item.Price} cp";
                        
                        int paddingSpaces = Math.Max(2, 22 - GetDisplayWidth(rawDisplayStr) - 3); 
                        string itemStr = $"[c/{idColor}:({item.Id})] {itemTag} {priceDisplayTag}" + new string(' ', paddingSpaces);
                        line += itemStr;
                    }
                    args.Player.SendMessage(line, 255, 255, 255); 
                }

                args.Player.SendInfoMessage("");
                args.Player.SendInfoMessage("[c/FF7E7E:(注：红色序号为限购商品)]");
                args.Player.SendInfoMessage("==================================================");
                args.Player.SendMessage($"[c/FFFFFF:灵韵余额：][c/A32CC4:{charmPoints} cp] [c/FFFFFF:|] [c/55CDFF:第 3 页] [c/FFFFFF:(输入] /epshop 1 [c/FFFFFF:返回道具商店)]", 85, 210, 132);
            }
        }
        
        /// <summary>
        /// 指令 /epbuy [商品序号] [购买次数]：购买商品
        /// 支持普通道具、盲盒、神秘商店商品，处理限购、折扣保底、背包空间检查及积分扣减
        /// 购买盲盒时广播开启结果
        /// </summary>
        public static void EpBuyCommand(CommandArgs args)
        {
            if (!args.Player.Active || !args.Player.IsLoggedIn)
            {
                args.Player.SendErrorMessage("[Epoint] 请先登录");
                return;
            }
            TryDailySignIn(args.Player, args.Player.Account.Name);

            if (args.Parameters.Count < 1 || !int.TryParse(args.Parameters[0], out int shopId))
            {
                args.Player.SendErrorMessage("语法错误。正确用法: /epbuy <商品序号> [购买次数]");
                return;
            }

            int buyTimes = 1; 
            if (args.Parameters.Count > 1 && (!int.TryParse(args.Parameters[1], out buyTimes) || buyTimes <= 0))
            {
                args.Player.SendErrorMessage("无效的购买次数。");
                return;
            }

            var shopItem = Epoint.Config.ShopItems.FirstOrDefault(i => i.Id == shopId);
            var blindBox = BlindBoxes.FirstOrDefault(b => b.Id == shopId);
            var mysticItem = Epoint.Config.MysticShopItems.FirstOrDefault(i => i.Id == shopId);

            if (shopItem == null && blindBox == null && mysticItem == null)
            {
                args.Player.SendErrorMessage("无效的商品序号。");
                return;
            }
            
            string accountName = args.Player.Account.Name;
            var data = Epoint.Data.GetPlayerData(accountName);

            // 神秘商店独立结算体系（消耗cp）
            if (mysticItem != null)
            {
                bool isUnlocked;
                lock (data.SyncLock) { isUnlocked = data.IsMysticShopUnlocked; }
                
                if (!isUnlocked)
                {
                    args.Player.SendErrorMessage("无效的商品序号。"); 
                    return;
                }
                
                if (mysticItem.IsOneTime)
                {
                    if (buyTimes > 1) { args.Player.SendErrorMessage($"【[c/A32CC4:{mysticItem.Name}]】只能购买 1 份。"); return; }
                    if (Epoint.Data.HasPurchasedOneTimeItem(accountName, mysticItem.ItemNetId)) { args.Player.SendErrorMessage($"购买失败：限购一次。"); return; }
                }
                
                int requiredSlots = buyTimes; 
                bool hasEmptySpace = args.Player.TPlayer.inventory.Take(50).Count(i => i == null || i.IsAir) >= requiredSlots;
                if (!hasEmptySpace) { args.Player.SendErrorMessage("购买失败：背包空间不足，请先清理！"); return; }
                
                int totalCpCost = mysticItem.Price * buyTimes;
                
                lock (data.SyncLock)
                {
                    if (data.CharmPoints < totalCpCost)
                    {
                        args.Player.SendErrorMessage("购买失败：灵韵积分不足！");
                        return;
                    }
                    data.CharmPoints -= totalCpCost;
                }
                
                args.Player.GiveItem(mysticItem.ItemNetId, mysticItem.Stack * buyTimes);
                if (mysticItem.IsOneTime) Epoint.Data.RecordPurchase(accountName, mysticItem.ItemNetId); 
                
                args.Player.SendSuccessMessage($"[c/A32CC4:成功购买 {buyTimes} 份 {mysticItem.Name}，共消费 {totalCpCost} cp！]");
                
                Epoint.Data.SavePlayerData(data); 
                Task.Run(() => Epoint.Data.FlushAll()); 
                return;
            }

            // 基础商店结算体系（ep）
            if (blindBox != null && !Main.hardMode)
            {
                args.Player.SendErrorMessage("购买失败：盲盒在击败血肉墙后才能购买！");
                return;
            }

            int reqSlots = blindBox != null ? buyTimes : 1; 
            bool hasEmpty = args.Player.TPlayer.inventory.Take(50).Count(i => i == null || i.IsAir) >= reqSlots;
            if (!hasEmpty)
            {
                args.Player.SendErrorMessage("购买失败：背包空间不足，请先清理！");
                return;
            }
            
            if (shopItem is { IsOneTime: true })
            {
                if (buyTimes > 1) { args.Player.SendErrorMessage($"【[c/B0E0E6:{shopItem.Name}]】只能购买 1 份。"); return; }
                if (Epoint.Data.HasPurchasedOneTimeItem(accountName, shopItem.ItemNetId)) { args.Player.SendErrorMessage($"购买失败：限购一次。"); return; }
            }

            var myPityMisses = DiscountMisses.GetOrAdd(accountName, _ => new Dictionary<int, int>());
            var myActiveDiscounts = ActiveDiscounts.GetOrAdd(accountName, _ => new Dictionary<int, bool>());

            int totalCost = 0;
            int successfulBuys = 0;
            int pityTriggers = 0;
            int basePrice = shopItem?.Price ?? blindBox?.Price ?? 0;

            // 批量购买时一次性锁住玩家数据，避免多次扣款并发问题
            lock (data.SyncLock)
            {
                double vipMult = GetMembership(data).Discount;

                for (int k = 0; k < buyTimes; k++)
                {
                    bool hasPityDiscount = shopItem is { IsOneTime: false } && myActiveDiscounts.GetValueOrDefault(shopItem.Id, false);
                    double finalMult = hasPityDiscount ? vipMult * 0.70 : vipMult;
                    int unitPrice = (int)(basePrice * finalMult);

                    if (data.Points < unitPrice) break; 

                    data.Points -= unitPrice;
                    data.TotalSpent += unitPrice;
                    totalCost += unitPrice;
                    successfulBuys++;

                    if (shopItem is { IsOneTime: false })
                    {
                        if (hasPityDiscount)
                        {
                            myActiveDiscounts[shopItem.Id] = false;
                            myPityMisses[shopItem.Id] = 0; 
                        }
                        
                        if (!myActiveDiscounts.GetValueOrDefault(shopItem.Id, false))
                        {
                            int misses = myPityMisses.GetValueOrDefault(shopItem.Id, 0);
                            double prob = misses < 4 ? 0.10 : 0.10 + (misses - 3) * 0.05;
                            
                            if (Rand.NextDouble() < prob)
                            {
                                myActiveDiscounts[shopItem.Id] = true;
                                myPityMisses[shopItem.Id] = 0;
                                pityTriggers++;
                            }
                            else myPityMisses[shopItem.Id] = misses + 1;
                        }
                    }
                }
                data.VipLevel = GetMembership(data).Name; 
                if (shopItem != null && successfulBuys > 0) data.PurchasedNormalItems.Add(shopItem.Id);
            }

            if (successfulBuys == 0)
            {
                args.Player.SendErrorMessage("购买失败：积分不足！(๑´ㅁ`)");
                return;
            }

            if (shopItem != null)
            {
                args.Player.GiveItem(shopItem.ItemNetId, shopItem.Stack * successfulBuys);
                if (shopItem.IsOneTime) Epoint.Data.RecordPurchase(accountName, shopItem.ItemNetId); 

                args.Player.SendSuccessMessage($"[c/00FF00:购买了 {successfulBuys} 份 {shopItem.Name}，共消费 {totalCost} ep！]");
                if (pityTriggers > 0)
                    args.Player.SendSuccessMessage($"本次购买触发了 {pityTriggers} 次幸运 [c/FFD700:7折] 优惠！");
            }
            else if (blindBox != null)
            {
                for (int k = 0; k < successfulBuys; k++)
                {
                    double r = Rand.NextDouble(); 
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
                    selectedDrop ??= blindBox.DropPool.Last();

                    args.Player.GiveItem(selectedDrop.ItemId, selectedDrop.Stack);
                    string dropTag = selectedDrop.Stack > 1 ? $"[i/s{selectedDrop.Stack}:{selectedDrop.ItemId}]" : $"[i:{selectedDrop.ItemId}]";
                    string boxNameDisplay = blindBox.Id == 107 ? BuildAnimatedGradientText(blindBox.Name) : $"[c/{blindBox.ColorHex}:{blindBox.Name}]";
                    TSPlayer.All.SendMessage($"[c/55CDFF:{accountName}] 开启了 {boxNameDisplay}，获得了 {dropTag}", 255, 255, 255);
                }
                args.Player.SendSuccessMessage($"[c/00FF00:开启了 {successfulBuys} 个盲盒，共消费 {totalCost} ep！]");
            }
            
            Epoint.Data.SavePlayerData(data); 
            Task.Run(() => Epoint.Data.FlushAll()); 
        }

        /// <summary> 指令 /eprank：异步读取所有玩家数据，按积分排序显示前十名 </summary>
        public static void EpRankCommand(CommandArgs args)
        {
            Task.Run(() =>
            {
                try
                {
                    string playersDir = Path.Combine(TShock.SavePath, "epoint", "players");
                    if (!Directory.Exists(playersDir)) return;

                    var files = Directory.GetFiles(playersDir, "*.json");
                    var rankList = new List<dynamic>(); 

                    foreach (var file in files)
                    {
                        try
                        {
                            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var reader = new StreamReader(stream);
                            string json = reader.ReadToEnd();
                            var account = JsonConvert.DeserializeObject<dynamic>(json);
                            if (account != null) rankList.Add(account);
                        }
                        catch (Exception ex) { TShock.Log.ConsoleError($"[Epoint] 跳过读取排行榜文件: {ex.Message}"); }
                    }

                    var topPlayers = rankList.OrderByDescending(p => (long)p.Points).Take(10).ToList();
                    args.Player.SendMessage("======== Epoint 积分排行榜 ========", 255, 215, 0);
                    
                    if (topPlayers.Count == 0)
                    {
                        args.Player.SendInfoMessage("暂无玩家数据");
                        return;
                    }

                    for (int i = 0; i < topPlayers.Count; i++)
                    {
                        string rankColor = i switch { 0 => "FFD700", 1 => "C0C0C0", 2 => "CD7F32", _ => "FFFFFF" };
                        args.Player.SendMessage($"[c/{rankColor}:Top {i + 1}.] {topPlayers[i].PlayerName} - [c/00FF00:{topPlayers[i].Points} ep]", 255, 255, 255);
                    }
                }
                catch (Exception ex)
                {
                    args.Player.SendErrorMessage($"[Epoint] 排行榜生成失败: {ex.Message}");
                }
            });
        }
        
        // ================= 自动事件钩子监听 =================

        /// <summary> 玩家登录时重置在线时长计数并执行自动签到 </summary>
        public static void OnPlayerLogin(PlayerPostLoginEventArgs args)
        {
            var player = args.Player;
            if (!player.Active) return;

            string accountName = player.Account.Name;
            SessionTime[accountName] = 0; 
            TryDailySignIn(player, accountName, true); 
        }
        
        /// <summary>
        /// 在线奖励定时器回调：为每个在线玩家发放周期奖励（基础10ep，有暴击概率），同时尝试发放灵韵积分（cp）
        /// 设有每日积分上限，使用 lock 保证数据安全
        /// </summary>
        private static void OnOnlineTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            var activeAccounts = TShock.Players.Where(p => p is { Active: true, IsLoggedIn: true }).Select(p => p.Account.Name).ToHashSet();
            foreach (var account in SessionTime.Keys) if (!activeAccounts.Contains(account)) SessionTime.TryRemove(account, out _);

            foreach (var player in TShock.Players)
            {
                if (player == null || !player.Active || !player.IsLoggedIn) continue;

                string accountName = player.Account.Name;
                TryDailySignIn(player, accountName);
                
                SessionTime.AddOrUpdate(accountName, Epoint.Config.OnlineRewardInterval, (_, v) => v + Epoint.Config.OnlineRewardInterval);

                var data = Epoint.Data.GetPlayerData(accountName);
                
                int dailyCap;
                int actualReward = 0;
                double randLucky = Rand.NextDouble();
                int baseOnlineReward;
                string msgPrefix;

                if (randLucky < 0.04) { baseOnlineReward = 50; msgPrefix = "[c/87CEEB:在线奖励超级暴击！！]"; }
                else if (randLucky < 0.14) { baseOnlineReward = 30; msgPrefix = "[c/87CEEB:在线奖励暴击！]"; }
                else { baseOnlineReward = 10; msgPrefix = "[c/87CEEB:在线奖励]"; }

                int theoreticalReward = Epoint.Config.FastPacedMode ? baseOnlineReward * 2 : baseOnlineReward;

                lock (data.SyncLock)
                {
                    int baseCap = Epoint.Config.FastPacedMode ? Epoint.Config.BaseDailyCap * 2 : Epoint.Config.BaseDailyCap;
                    dailyCap = (int)(0.9 * baseCap + 0.1 * baseCap * data.TotalDays);
                    int remainingCap = dailyCap - data.PointsToday;
                    
                    int cpMisses = CpMisses.GetValueOrDefault(accountName, 0);
                    double cpProb = cpMisses == 5 ? 0.40 : 0.02; 
                    
                    if (Rand.NextDouble() < cpProb)
                    {
                        CpMisses[accountName] = 0;
                        data.CharmPoints += 1;
                        
                        player.SendMessage($"[c/A32CC4:这是什么？灵韵积分！获得了] [c/00FF00:1] [c/A32CC4:cp]", 255, 255, 255);
                        
                        if (!data.IsMysticShopUnlocked && data.CharmPoints >= 10)
                        {
                            data.IsMysticShopUnlocked = true; 
                            player.SendMessage($"[c/A32CC4:Epoint 【神秘商店】已永久开启！快去] [c/00FF00:/epshop 3] [c/A32CC4:看看吧~]", 255, 255, 255);
                        }
                    }
                    else
                    {
                        cpMisses++;
                        if (cpMisses >= 6) cpMisses = 0; 
                        CpMisses[accountName] = cpMisses;
                    }

                    if (remainingCap > 0) 
                    {
                        actualReward = Math.Min(theoreticalReward, remainingCap);
                        data.Points += actualReward;
                        data.TotalEarned += actualReward; 
                        data.PointsToday += actualReward;
                        data.VipLevel = GetMembership(data).Name;
                    }
                }
                
                Epoint.Data.SavePlayerData(data); 

                if (actualReward > 0)
                {
                    string capSuffix = (data.PointsToday >= dailyCap) ? " [c/FF0000:(今日获取积分已达上限！)]" : "";
                    player.SendSuccessMessage($"[c/55CDFF:叮咚～(∠・ω< )⌒★] {msgPrefix} [c/FFD700:{actualReward}] ep{capSuffix}");
                }
            }
        }

        /// <summary> 玩家离开时清理其相关缓存数据并触发数据刷写 </summary>
        public static void OnPlayerLeave(LeaveEventArgs args)
        {
            var player = TShock.Players[args.Who];
            if (player is { Active: true, IsLoggedIn: true })
            {
                SessionTime.TryRemove(player.Account.Name, out _);
                PersonalKillTracker.TryRemove(player.Account.Name, out _);
                DiscountMisses.TryRemove(player.Account.Name, out _);
                ActiveDiscounts.TryRemove(player.Account.Name, out _);
                CpMisses.TryRemove(player.Account.Name, out _);
                
                Epoint.Data.FlushAndRemove(player.Account.Name);
            }
        }
        
        /// <summary>
        /// NPC受击钩子：记录玩家对Boss或类Boss（如世吞）的伤害，用于最终奖励分配
        /// 对于普通Boss使用原版的NPCDamageTracker统计，对于类Boss使用自定义追踪
        /// </summary>
        public static void OnNpcStrike(object? sender, GetDataHandlers.NPCStrikeEventArgs args)
        {
            var player = args.Player;
            var npc = Main.npc[args.ID];

            if (!player.Active || !player.IsLoggedIn || !npc.active) return;
            
            bool isEow = npc.netID is 13 or 14 or 15;
            
            // 处理类Boss（非标准Boss也使用Boss倍率表的实体）
            if (!npc.boss && (BossBasePoolMultipliers.ContainsKey(npc.netID) || isEow))
            {
                int bossKey = isEow ? -13 : (npc.realLife >= 0 ? npc.realLife : npc.whoAmI);
                BossSpawnTracker.GetOrAdd(bossKey, _ => TShock.Players.Count(p => p is { Active: true }));
                
                var dict = CustomDamageTracker.GetOrAdd(bossKey, _ => new ConcurrentDictionary<string, int>());
                dict.AddOrUpdate(player.Account.Name, args.Damage, (_, v) => v + args.Damage);
                return;
            }
            
            if (npc.boss)
            {
                int bossKey = npc.realLife >= 0 ? npc.realLife : npc.whoAmI;
                BossSpawnTracker.GetOrAdd(bossKey, _ => TShock.Players.Count(p => p is { Active: true }));
            }
        }

        /// <summary>
        /// 原版Boss击杀钩子（通过 On.Terraria.GameContent.BossDamageTracker 触发）
        /// 从原版伤害统计中读取玩家伤害占比，并调用 GrantBossReward 发放奖励
        /// </summary>
        private static void NativeBossDamageTracker_OnBossKilled(BossDamageTracker.orig_OnBossKilled orig, Terraria.GameContent.BossDamageTracker self, NPC npc)
        {
            orig(self, npc); 
            
            int netId = npc.netID;
            int bossKey = npc.realLife >= 0 ? npc.realLife : npc.whoAmI;
            
            int pCount = BossSpawnTracker.TryRemove(bossKey, out int count) ? count : TShock.Players.Count(p => p is { Active: true });
            
            double baseMult = GetBossBaseMultiplier(netId);
            double mpMult = pCount == 1 ? 1.0 : (pCount == 2 ? 1.6 : (pCount == 3 ? 2.1 : (pCount == 4 ? 2.4 : 2.5)));
            string bossName = npc.FullName;

            int[] damagePercentages = Terraria.GameContent.NPCDamageTracker.CalculatePercentages(self._list.Select(x => x.Damage).ToArray());

            for (int i = 0; i < self._list.Count; i++)
            {
                var entry = self._list[i];
                if (entry is Terraria.GameContent.NPCDamageTracker.PlayerCreditEntry playerEntry)
                {
                    int percentage = damagePercentages[i];
                    if (percentage > 0)
                    {
                        var tsPlayer = TShock.Players.FirstOrDefault(p => p is { Active: true, IsLoggedIn: true } && p.Name == playerEntry.PlayerName);
                        if (tsPlayer != null)
                            GrantBossReward(tsPlayer, tsPlayer.Account.Name, baseMult, mpMult, percentage / 100.0, bossName);
                    }
                }
            }
        }

        /// <summary>
        /// NPC死亡事件钩子
        /// 处理类Boss的击杀奖励分配（基于自定义伤害追踪）
        /// 处理普通小怪的50击杀里程碑奖励。
        /// </summary>
        public static void OnNpcKilled(NpcKilledEventArgs args)
        {
            var npc = args.npc;
            int netId = npc.netID;
            bool isEow = netId is 13 or 14 or 15;

            // 处理类Boss击杀（世吞或配置表里的非标准Boss）
            if (!npc.boss && (BossBasePoolMultipliers.ContainsKey(netId) || isEow))
            {
                int bossKey = isEow ? -13 : (npc.realLife >= 0 ? npc.realLife : npc.whoAmI);
                
                if (isEow)
                {
                    bool eowAlive = Main.npc.Any(n => n is { active: true, netID: 13 or 14 or 15 } && n.whoAmI != npc.whoAmI);
                    if (eowAlive) return;
                }

                if (CustomDamageTracker.TryRemove(bossKey, out var dmgDict))
                {
                    int pCount = BossSpawnTracker.TryRemove(bossKey, out int count) ? count : TShock.Players.Count(p => p is { Active: true });
                    double baseMult = GetBossBaseMultiplier(isEow ? -13 : netId);
                    double mpMult = pCount == 1 ? 1.0 : (pCount == 2 ? 1.6 : (pCount == 3 ? 2.1 : (pCount == 4 ? 2.4 : 2.5)));
                    string bossName = isEow ? "世界吞噬怪" : npc.FullName;
                    
                    int maxHp = isEow ? (int)(10000 * (1.0 + Math.Max(0, pCount - 1) * 0.35)) : npc.lifeMax;
                    // int totalPlayerDamage = dmgDict.Values.Sum();
                    // int trapDamage = Math.Max(0, maxHp - totalPlayerDamage); (已弃用的陷阱和环境伤害统计)
                    
                    var activePlayers = TShock.Players.Where(p => p is { Active: true, IsLoggedIn: true, Account: not null }).ToList();
                    
                    foreach (var kvp in dmgDict)
                    {
                        double damagePercent = Math.Floor((double)kvp.Value / maxHp * 100) / 100.0;
                        if (damagePercent > 0)
                        {
                            var tsPlayer = activePlayers.FirstOrDefault(pl => pl.Account.Name == kvp.Key);
                            if (tsPlayer != null) GrantBossReward(tsPlayer, kvp.Key, baseMult, mpMult, damagePercent, bossName);
                        }
                    }
                }
                return;
            }

            if (npc.boss) return;

            // 过滤掉友好NPC、低生命值NPC、雕像生成的NPC
            if (npc.friendly || npc.lifeMax < 5 || npc.SpawnedFromStatue) return;
            
            int killerIndex = npc.lastInteraction; 
            if (killerIndex < 0 || killerIndex >= 255) return; 

            var killerPlayer = TShock.Players[killerIndex];
            if (killerPlayer == null || !killerPlayer.Active || !killerPlayer.IsLoggedIn) return;

            string killerAccount = killerPlayer.Account.Name;
            TryDailySignIn(killerPlayer, killerAccount);

            var killDict = PersonalKillTracker.GetOrAdd(killerAccount, _ => new ConcurrentDictionary<int, int>());
            int personalKills = killDict.AddOrUpdate(netId, 1, (_, v) => v + 1);
            
            // 每击杀50只同种怪物触发一次里程碑奖励
            if (personalKills > 0 && personalKills % 50 == 0)
            {
                var data = Epoint.Data.GetPlayerData(killerAccount);

                int baseLifeMax = Terraria.ID.ContentSamples.NpcsByNetId[netId].lifeMax;
                int baseMobReward = (int)(200 * (1 - Math.Exp(-baseLifeMax / 250.0)));
                baseMobReward = Math.Clamp(baseMobReward, 50, 150);
                
                int actualReward = 0;
                int dailyCap;
                
                lock (data.SyncLock)
                {
                    int baseCap = Epoint.Config.FastPacedMode ? Epoint.Config.BaseDailyCap * 2 : Epoint.Config.BaseDailyCap;
                    dailyCap = (int)(0.9 * baseCap + 0.1 * baseCap * data.TotalDays);
                    
                    int theoreticalReward = Epoint.Config.FastPacedMode ? baseMobReward * 2 : baseMobReward;
                    int remainingCap = dailyCap - data.PointsToday;
                    
                    if (remainingCap > 0)
                    {
                        actualReward = Math.Min(theoreticalReward, remainingCap);
                        data.Points += actualReward;
                        data.TotalEarned += actualReward; 
                        data.PointsToday += actualReward;
                        data.VipLevel = GetMembership(data).Name;
                    }
                }

                if (actualReward <= 0) return;

                Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    if (killerPlayer is { Active: true, IsLoggedIn: true } && killerPlayer.Account.Name == killerAccount)
                    {
                        Epoint.Data.SavePlayerData(data);
                        string capSuffix = (data.PointsToday >= dailyCap) ? " [c/FF0000:(今日获取积分已达上限！)]" : "";
                        killerPlayer.SendSuccessMessage($"[c/FFD700:达成里程碑！(ง๑ •̀_•́)ง] 击败了 {personalKills} 只 [c/B0E0E6:{npc.FullName}]，奖励 {actualReward} ep{capSuffix}");
                    }
                });
            }
        }
        
        /// <summary>
        /// 发放Boss击杀奖励的核心逻辑
        /// 根据玩家伤害占比、Boss基础倍率、多人倍率、玩家等级上限等计算实际奖励积分
        /// 注意：存在复杂上限裁剪逻辑，可能需重构 
        /// </summary>
        private static void GrantBossReward(TSPlayer player, string accountName, double baseMult, double mpMult, double percent, string bossName)
        {
            TryDailySignIn(player, accountName);
            
            var data = Epoint.Data.GetPlayerData(accountName);
            
            int actualReward = 0;
            int dailyCap;

            lock (data.SyncLock)
            {
                int baseCap = Epoint.Config.FastPacedMode ? Epoint.Config.BaseDailyCap * 2 : Epoint.Config.BaseDailyCap;
                dailyCap = (int)(0.9 * baseCap + 0.1 * baseCap * data.TotalDays);

                double playerCap = 0.2 * baseCap + 0.04 * baseCap * data.TotalDays;
                double effectiveBaseMult = baseMult;
                
                // TODO: 该条件逻辑意图限制单次Boss奖励不超过玩家个人上限，但实现较晦涩，建议重构
                if (playerCap <= baseCap)
                {
                    if (baseMult * baseCap > playerCap) effectiveBaseMult = playerCap / baseCap; 
                }

                int theoreticalReward = (int)(effectiveBaseMult * baseCap * mpMult * percent);
                int remainingCap = dailyCap - data.PointsToday;
                
                if (remainingCap > 0)
                {
                    actualReward = Math.Min(theoreticalReward, remainingCap);
                    data.Points += actualReward;
                    data.TotalEarned += actualReward; 
                    data.PointsToday += actualReward;
                    data.VipLevel = GetMembership(data).Name;
                }
            }

            if (actualReward > 0)
            {
                Epoint.Data.SavePlayerData(data);
                string capSuffix = (data.PointsToday >= dailyCap) ? " [c/FF0000:(今日获取积分已达上限！)]" : "";
                player.SendSuccessMessage($"[c/FFD700:恭喜(੭ु´ ᐜ `)੭ु⁾⁾] 成功击败[c/B0E0E6:{bossName}]，奖励 [c/FFD700:{actualReward}] ep{capSuffix}");
            }
        }
    }
}