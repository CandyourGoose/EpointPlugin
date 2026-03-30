using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using System.Text.RegularExpressions; // 【新增】引入正则表达式命名空间

namespace EpointPlugin
{
    // ==================================================
    // [ShopItem 商品实体类] 定义每件商品在代码和 JSON 中的字段
    // ==================================================
    public class ShopItem
    {
        [JsonProperty("序号")] public int Id { get; set; }
        [JsonProperty("商品名称")] public string Name { get; set; } = "";
        [JsonProperty("原版物品ID")] public int ItemNetId { get; set; } 
        [JsonProperty("积分价格")] public int Price { get; set; }
        [JsonProperty("物品数量")] public int Stack { get; set; } 
        [JsonProperty("是否限购")] public bool IsOneTime { get; set; } 
    }

    // 【优化】盲盒配置类：废弃了序号，直接使用名称绑定，更直观
    public class BlindBoxPriceConfig
    {
        [JsonProperty("盲盒名称")] public string Name { get; set; } = "";
        [JsonProperty("积分价格")] public int Price { get; set; }
    }
    
    // ============================================================
    // [EpointConfig 全局配置类] 负责管理全局开关、数值设定和自定义商品列表
    // ============================================================
    public class EpointConfig
    {
        // 全局功能默认配置
        [JsonProperty("快节奏模式(设为true则积分获取效率及上限翻倍, 适用于进度推进快的高手玩家, 默认false)")] public bool FastPacedMode { get; set; } 
        [JsonProperty("每日积分获取基础上限(默认1000)")] public int BaseDailyCap { get; set; } = 1000;
        [JsonProperty("在线奖励间隔(分钟, 默认10)")] public int OnlineRewardInterval { get; set; } = 10;
        [JsonProperty("Boss奖励积分分配方式(按贡献分配true|平均分配false, 默认true)")] public bool BossRewardByDamage { get; set; } = true;
        
        // 商品列表默认配置
        [JsonProperty("道具商店列表（支持自定义商品）")] 
        public List<ShopItem> ShopItems { get; set; } = 
        [
            new ShopItem { Id = 1, Name = "铂金币", ItemNetId = 74, Price = 999, Stack = 1, IsOneTime = false },
            new ShopItem { Id = 2, Name = "生命水晶", ItemNetId = 29, Price = 200, Stack = 1, IsOneTime = false },
            new ShopItem { Id = 3, Name = "耍蛇者长笛", ItemNetId = 4262, Price = 1000, Stack = 1, IsOneTime = true },
            new ShopItem { Id = 4, Name = "虫洞药水", ItemNetId = 2997, Price = 500, Stack = 10, IsOneTime = false },
            new ShopItem { Id = 5, Name = "强效治疗药水", ItemNetId = 499, Price = 1500, Stack = 10, IsOneTime = false },
            new ShopItem { Id = 6, Name = "超级治疗药水", ItemNetId = 3544, Price = 2500, Stack = 10, IsOneTime = false },
            new ShopItem { Id = 7, Name = "学徒诱饵", ItemNetId = 2674, Price = 100, Stack = 10, IsOneTime = false },
            new ShopItem { Id = 8, Name = "熟手诱饵", ItemNetId = 2675, Price = 300, Stack = 10, IsOneTime = false },
            new ShopItem { Id = 9, Name = "大师诱饵", ItemNetId = 2676, Price = 800, Stack = 10, IsOneTime = false },
            new ShopItem { Id = 10, Name = "史莱姆法杖", ItemNetId = 1309, Price = 2000, Stack = 1, IsOneTime = true },
            new ShopItem { Id = 11, Name = "捣蛋猫", ItemNetId = 5663, Price = 4000, Stack = 1, IsOneTime = true },
            new ShopItem { Id = 12, Name = "火绒狐", ItemNetId = 5664, Price = 5000, Stack = 1, IsOneTime = true },
            new ShopItem { Id = 13, Name = "骨镐", ItemNetId = 1320, Price = 2000, Stack = 1, IsOneTime = true },
            new ShopItem { Id = 14, Name = "混沌传送杖", ItemNetId = 1326, Price = 8000, Stack = 1, IsOneTime = true },
            new ShopItem { Id = 15, Name = "女巫扫帚", ItemNetId = 4444, Price = 12000, Stack = 1, IsOneTime = true }
        ];
        
        [JsonProperty("盲盒商店列表（仅支持自定义价格）")] 
        public List<BlindBoxPriceConfig> BlindBoxPrices { get; set; } = 
        [
            new BlindBoxPriceConfig { Name = "普通盲盒", Price = 500 },
            new BlindBoxPriceConfig { Name = "冰雪盲盒", Price = 500 },
            new BlindBoxPriceConfig { Name = "丛林盲盒", Price = 750 },
            new BlindBoxPriceConfig { Name = "腐化盲盒", Price = 750 },
            new BlindBoxPriceConfig { Name = "猩红盲盒", Price = 750 },
            new BlindBoxPriceConfig { Name = "神圣盲盒", Price = 750 },
            new BlindBoxPriceConfig { Name = "奇异染料盲盒", Price = 750 }
        ];
        
        // 自动生成默认配置 json 文件
        public static EpointConfig Read(string filePath)
        {
            if (!File.Exists(filePath))
            {
                var defaultConfig = new EpointConfig();
                
                string json = JsonConvert.SerializeObject(defaultConfig, Formatting.Indented);
                
                json = Regex.Replace(json, @"\{\r?\n\s+([^\[\]\{\}]*?)\r?\n\s+\}", m => 
                {
                    string content = m.Groups[1].Value;
                    content = Regex.Replace(content, @"\r?\n\s+", " "); // 去掉内部换行
                    return "{ " + content + " }";
                });
                
                File.WriteAllText(filePath, json);
                return defaultConfig;
            }

            try
            {
                string existingJson = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<EpointConfig>(existingJson) ?? new EpointConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Epoint] 配置文件错误，请检查 JSON 格式！错误信息: {ex.Message}");
                return new EpointConfig(); 
            }
        }
    }
}