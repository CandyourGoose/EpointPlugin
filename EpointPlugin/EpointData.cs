using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using TShockAPI;

namespace EpointPlugin
{
    // =========================================================================
    // [玩家数据实体]
    // =========================================================================
    public class PlayerAccount
    {
        // 【核心修复1】对象级线程锁（不参与 JSON 序列化），确保该玩家数据在多线程下绝对安全
        [JsonIgnore] public readonly object SyncLock = new(); 

        [JsonProperty("【注意事项】", Order = -999)] 
        public string Warning { get; set; } = "【本文件仅供管理员查询/管理玩家数据，若非必要请勿随意修改！】";
        [JsonProperty("玩家名", Order = 1)] public string PlayerName { get; set; } = "";
        [JsonProperty("会员等级", Order = 2)] public string VipLevel { get; set; } = "";
        [JsonProperty("积分余额", Order = 3)] public int Points { get; set; }
        [JsonProperty("灵韵积分余额", Order = 4)] public int CharmPoints { get; set; }
        [JsonProperty("当日已获取积分", Order = 5)] public int PointsToday { get; set; }
        [JsonProperty("累计登录天数", Order = 6)] public int TotalDays { get; set; }
        [JsonProperty("连续签到天数", Order = 7)] public int StreakDays { get; set; }
        [JsonProperty("上次登录日期", Order = 8)] public string LastLoginDate { get; set; } = ""; 
        [JsonProperty("积分总收入", Order = 9)] public long TotalEarned { get; set; } 
        [JsonProperty("积分总支出", Order = 10)] public long TotalSpent { get; set; }  
        [JsonProperty("已购限购商品记录", Order = 11)] public List<int> PurchasedItems { get; set; } = new List<int>();
        [JsonProperty("已购普通商品记录", Order = 12)] public HashSet<int> PurchasedNormalItems { get; set; } = new HashSet<int>(); 
        [JsonProperty("神秘商店是否解锁", Order = 13)] public bool IsMysticShopUnlocked { get; set; }
    }
    
    // =========================================================================
    // [读写模块] 带有高并发内存缓存与线程锁的安全 IO 管理器
    // =========================================================================
    public class EpointData
    {
        private readonly string _playersDir;
        private readonly ConcurrentDictionary<string, PlayerAccount> _cache = new();

        public EpointData(string basePath)
        {
            _playersDir = Path.Combine(basePath, "players");
            if (!Directory.Exists(_playersDir)) Directory.CreateDirectory(_playersDir);
        }

        private string GetFilePath(string accountName)
        {
            // 【核心修复5】使用严苛的正则白名单，防止任何恶意的路径穿越或乱码
            string safeName = Regex.Replace(accountName, @"[^\w\-\u4e00-\u9fa5]", "_");
            return Path.Combine(_playersDir, $"{safeName}.json");
        }

        public PlayerAccount GetPlayerData(string accountName)
        {
            if (_cache.TryGetValue(accountName, out var cachedData))
                return cachedData;

            string path = GetFilePath(accountName);
            PlayerAccount? data = null; 
            
            if (File.Exists(path))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    string json = reader.ReadToEnd();
                    data = JsonConvert.DeserializeObject<PlayerAccount>(json);
                }
                catch (Exception ex)
                {
                    // 【核心修复8】使用 TShock 标准日志输出
                    TShock.Log.ConsoleError($"[Epoint] 读取玩家 {accountName} 的数据时发生异常: {ex.Message} (系统将生成保护性空白档案)");
                }
            }
            
            // 【防御3说明】这里就是极度安全的防 null 托底，无需担心反序列化失败
            data ??= new PlayerAccount { PlayerName = accountName };
            _cache.TryAdd(accountName, data); 
            return data;
        }

        public void SavePlayerData(PlayerAccount data)
        {
            _cache[data.PlayerName] = data; 
        }

        public void FlushAndRemove(string accountName)
        {
            if (_cache.TryRemove(accountName, out var data))
            {
                // 【核心修复4】写入文件前锁定该玩家的数据对象，防止写到一半被别的线程篡改
                lock (data.SyncLock)
                {
                    try
                    {
                        string path = GetFilePath(accountName);
                        string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                        File.WriteAllText(path, json);
                    }
                    catch (Exception ex) { TShock.Log.ConsoleError($"[Epoint] 保存玩家 {accountName} 数据落盘失败: {ex.Message}"); }
                }
            }
        }

        public void FlushAll()
        {
            foreach (var kvp in _cache)
            {
                lock (kvp.Value.SyncLock)
                {
                    try
                    {
                        string path = GetFilePath(kvp.Key);
                        string json = JsonConvert.SerializeObject(kvp.Value, Formatting.Indented);
                        File.WriteAllText(path, json);
                    }
                    catch (Exception ex) { TShock.Log.ConsoleError($"[Epoint] 批量保存玩家 {kvp.Key} 数据落盘失败: {ex.Message}"); }
                }
            }
        }

        public int GetPoints(string accountName) => GetPlayerData(accountName).Points;

        public void AddPoints(string accountName, int amount)
        {
            var data = GetPlayerData(accountName);
            lock (data.SyncLock)
            {
                data.Points += amount;
                data.PointsToday += amount;
            }
        }

        public bool TryRemovePoints(string accountName, int amount)
        {
            var data = GetPlayerData(accountName);
            lock (data.SyncLock)
            {
                if (data.Points >= amount) 
                {
                    data.Points -= amount;
                    // 【核心修复2】补上了消费统计
                    data.TotalSpent += amount;
                    return true; 
                }
                return false; 
            }
        }

        public bool HasPurchasedOneTimeItem(string accountName, int shopId)
        {
            var data = GetPlayerData(accountName);
            lock (data.SyncLock) { return data.PurchasedItems.Contains(shopId); }
        }

        public void RecordPurchase(string accountName, int shopId)
        {
            var data = GetPlayerData(accountName);
            // 【核心修复7】锁定集合修改操作
            lock (data.SyncLock)
            {
                if (!data.PurchasedItems.Contains(shopId))
                    data.PurchasedItems.Add(shopId);
            }
        }
    }
}