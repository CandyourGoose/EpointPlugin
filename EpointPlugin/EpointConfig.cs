using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using System.Text.RegularExpressions; 

namespace EpointPlugin
{
    public class ShopItem
    {
        [JsonProperty("序号")] public int Id { get; set; }
        [JsonProperty("商品名称")] public string Name { get; set; } = "";
        [JsonProperty("原版物品ID")] public int ItemNetId { get; set; } 
        [JsonProperty("积分价格")] public int Price { get; set; }
        [JsonProperty("物品数量")] public int Stack { get; set; } 
        [JsonProperty("是否限购")] public bool IsOneTime { get; set; } 
    }

    public class BlindBoxPriceConfig
    {
        [JsonProperty("盲盒名称")] public string Name { get; set; } = "";
        [JsonProperty("积分价格")] public int Price { get; set; }
    }
    
    public class EpointConfig
    {
        [JsonProperty("快节奏模式(设为true则积分获取效率及上限翻倍, 适用于进度推进快的高手玩家, 默认false)")] public bool FastPacedMode { get; set; } 
        [JsonProperty("每日积分获取基础上限(默认1000)")] public int BaseDailyCap { get; set; } = 1000;
        [JsonProperty("在线奖励间隔(分钟, 默认10)")] public int OnlineRewardInterval { get; set; } = 10;
        [JsonProperty("Boss奖励积分分配方式(按贡献分配true|平均分配false, 默认true)")] public bool BossRewardByDamage { get; set; } = true;
        
        [JsonProperty("道具商店列表（支持自定义商品）", ObjectCreationHandling = ObjectCreationHandling.Replace)] 
        public List<ShopItem> ShopItems { get; set; } = 
        [
            new ShopItem { Id = 1, Name = "铂金币", ItemNetId = 74, Price = 999, Stack = 1, IsOneTime = false },
            new ShopItem { Id = 2, Name = "生命水晶", ItemNetId = 29, Price = 300, Stack = 1, IsOneTime = false },
            new ShopItem { Id = 3, Name = "耍蛇者长笛", ItemNetId = 4262, Price = 1200, Stack = 1, IsOneTime = true },
            new ShopItem { Id = 4, Name = "草药袋", ItemNetId = 3093, Price = 200, Stack = 1, IsOneTime = false },
            new ShopItem { Id = 5, Name = "强效治疗药水", ItemNetId = 499, Price = 750, Stack = 10, IsOneTime = false },
            new ShopItem { Id = 6, Name = "超级治疗药水", ItemNetId = 3544, Price = 1400, Stack = 10, IsOneTime = false },
            new ShopItem { Id = 7, Name = "虫洞药水", ItemNetId = 2997, Price = 150, Stack = 3, IsOneTime = false },
            new ShopItem { Id = 8, Name = "返回药水", ItemNetId = 4870, Price = 150, Stack = 1, IsOneTime = false },
            new ShopItem { Id = 9, Name = "大师诱饵", ItemNetId = 2676, Price = 900, Stack = 10, IsOneTime = false },
            new ShopItem { Id = 10, Name = "捣蛋猫", ItemNetId = 5663, Price = 2500, Stack = 1, IsOneTime = true },
            new ShopItem { Id = 11, Name = "火绒狐", ItemNetId = 5664, Price = 3500, Stack = 1, IsOneTime = true },
            new ShopItem { Id = 12, Name = "骨镐", ItemNetId = 1320, Price = 4000, Stack = 1, IsOneTime = true },
            new ShopItem { Id = 13, Name = "七彩草蛉", ItemNetId = 4961, Price = 1000, Stack = 1, IsOneTime = false },
            new ShopItem { Id = 15, Name = "女巫扫帚", ItemNetId = 4444, Price = 20000, Stack = 1, IsOneTime = true }
        ];
        
        [JsonProperty("盲盒商店列表（仅支持自定义价格）", ObjectCreationHandling = ObjectCreationHandling.Replace)] 
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
        
        [JsonProperty("神秘商店列表（支持自定义商品）", ObjectCreationHandling = ObjectCreationHandling.Replace)] 
        public List<ShopItem> MysticShopItems { get; set; } = 
        [
            new ShopItem { Id = 306, Name = "礼袋", ItemNetId = 1774, Price = 3, Stack = 1, IsOneTime = false },
            new ShopItem { Id = 307, Name = "礼物", ItemNetId = 1869, Price = 3, Stack = 1, IsOneTime = false },
            new ShopItem { Id = 301, Name = "史莱姆法杖", ItemNetId = 1309, Price = 10, Stack = 1, IsOneTime = true },
            new ShopItem { Id = 302, Name = "最土的块", ItemNetId = 5400, Price = 10, Stack = 1, IsOneTime = true },
            new ShopItem { Id = 304, Name = "万能晶塔", ItemNetId = 4951, Price = 20, Stack = 1, IsOneTime = false },
            new ShopItem { Id = 303, Name = "混沌传送杖", ItemNetId = 1326, Price = 25, Stack = 1, IsOneTime = true },
            new ShopItem { Id = 305, Name = "泰拉魔刃", ItemNetId = 4144, Price = 40, Stack = 1, IsOneTime = true }
        ];
        
        // 自动生成默认配置 config.json 文件
        public static EpointConfig Read(string filePath)
        {
            if (!File.Exists(filePath))
            {
                var defaultConfig = new EpointConfig();
                string json = JsonConvert.SerializeObject(defaultConfig, Formatting.Indented);
                
                json = Regex.Replace(json, @"\{\r?\n\s+([^\[\]\{\}]*?)\r?\n\s+\}", m => 
                {
                    string content = m.Groups[1].Value;
                    content = Regex.Replace(content, @"\r?\n\s+", " "); 
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
                Console.WriteLine($"[Epoint] 配置文件错误: {ex.Message}");
                return new EpointConfig(); 
            }
        }
    }
}