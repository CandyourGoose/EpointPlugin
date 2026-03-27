using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace EpointPlugin
{
    // =========================================================================
    // [玩家数据实体] 负责每个玩家在 /players/ 文件夹下的专属 .json 文件里的记录信息
    // =========================================================================
    public class PlayerAccount
    {
        [JsonProperty("玩家名")] public string PlayerName { get; set; } = "";
        [JsonProperty("积分余额")] public int Points { get; set; }
        [JsonProperty("今日已获取积分")] public int PointsToday { get; set; } // 用于每天计算上限时使用
        [JsonProperty("累计登录天数")] public int TotalDays { get; set; }
        [JsonProperty("连续签到天数")] public int StreakDays { get; set; }
        [JsonProperty("上次登录日期")] public string LastLoginDate { get; set; } = ""; // 格式：yyyy-MM-dd
        
        // 限购商品记录池：买过哪个商品的ID，就塞进这个列表里。下次买的时候查一下就知道买没买过了。
        [JsonProperty("已购买的限购商品(序号)")] public List<int> PurchasedItems { get; set; } = new List<int>();
    }
    
    // =========================================================================
    // [读写模块] 负责把 PlayerAccount 对象和硬盘里的文件进行互传操作
    // =========================================================================
    public class JsonDataManager
    {
        private readonly string _playersDir; // 玩家数据文件夹的绝对路径

        // 初始化时，确保存放玩家数据的 /players/ 文件夹存在
        public JsonDataManager(string basePath)
        {
            _playersDir = Path.Combine(basePath, "players");
            if (!Directory.Exists(_playersDir)) Directory.CreateDirectory(_playersDir);
        }

        // 安全路径转换工具：防止玩家起一些带特殊符号(如 / \ : *)的名字导致系统无法创建文件
        private string GetFilePath(string accountName)
        {
            string safeName = string.Join("_", accountName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_playersDir, $"{safeName}.json");
        }

        /// <summary>
        /// 读取玩家档案。每次读取都是直接读硬盘，因此外部修改文件后游戏内立刻生效！
        /// </summary>
        public PlayerAccount GetPlayerData(string accountName)
        {
            string path = GetFilePath(accountName);
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var data = JsonConvert.DeserializeObject<PlayerAccount>(json);
                    if (data != null) return data;
                }
                catch { /* 如果文件损坏，什么也不做，下面会返回一个全新的空档案防止崩溃 */ }
            }
            // 没找到文件或读取失败，返回一个带名字的新白板档案
            return new PlayerAccount { PlayerName = accountName };
        }

        /// <summary>
        /// 保存玩家档案。每次积分变动都会立刻写入硬盘。
        /// </summary>
        public void SavePlayerData(PlayerAccount data)
        {
            string path = GetFilePath(data.PlayerName);
            // Formatting.Indented 会让生成的 JSON 文件自动换行缩进，方便阅读
            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            File.WriteAllText(path, json);
        }

        // ================= 以下为提供给业务层调用的快捷操作接口 =================

        // 查玩家积分余额
        public int GetPoints(string accountName)
        {
            return GetPlayerData(accountName).Points;
        }

        // 添加积分（同时记录到今日总额中）
        public void AddPoints(string accountName, int amount)
        {
            var data = GetPlayerData(accountName);
            data.Points += amount;
            data.PointsToday += amount;
            SavePlayerData(data); // 记得每次改完要立刻存盘
        }

        // 扣除积分（如果是买东西，得先查查钱够不够）
        public bool TryRemovePoints(string accountName, int amount)
        {
            var data = GetPlayerData(accountName);
            if (data.Points >= amount) // 积分够
            {
                data.Points -= amount;
                SavePlayerData(data);
                return true; // 返回扣款成功
            }
            return false; // 积分不够，返回扣款失败
        }

        // 查玩家有没有买过限购商品
        public bool HasPurchasedOneTimeItem(string accountName, int shopId)
        {
            return GetPlayerData(accountName).PurchasedItems.Contains(shopId);
        }

        // 购买成功后，把这个商品序号记在玩家的“已购买限购商品记录池”上
        public void RecordPurchase(string accountName, int shopId)
        {
            var data = GetPlayerData(accountName);
            if (!data.PurchasedItems.Contains(shopId))
            {
                data.PurchasedItems.Add(shopId);
                SavePlayerData(data);
            }
        }
    }
}