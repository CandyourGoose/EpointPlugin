using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace EpointPlugin
{
    // =========================================================================
    // [ShopItem 商品实体类] 定义每一件商品在代码和 JSON 中的字段
    // =========================================================================
    public class ShopItem
    {
        // [JsonProperty("...")] 的作用是：告诉解析器，JSON里的中文名字对应代码里的哪个变量。
        [JsonProperty("序号")] public int Id { get; set; }
        [JsonProperty("商品名称")] public string Name { get; set; } = "";
        [JsonProperty("原版物品ID")] public int ItemNetId { get; set; } // 泰拉瑞亚原版的内部物品代码
        [JsonProperty("积分价格")] public int Price { get; set; }
        [JsonProperty("物品数量")] public int Stack { get; set; } // 购买一次给多少个（如药水给 5 个）
        [JsonProperty("是否限购")] public bool IsOneTime { get; set; } // true代表此商品每个玩家限购一次
    }
    
    // =========================================================================
    // [EpointConfig 全局配置类] 负责管理全局开关、数值设定和自定义商品列表
    // =========================================================================
    public class EpointConfig
    {
        // 全局配置项映射
        [JsonProperty("快节奏模式(设为true则积分获取效率及上限翻倍, 适用于进度推进快的高手玩家, 默认false)")] public bool FastPacedMode { get; set; } 
        [JsonProperty("每日积分获取基础上限(默认1000)")] public int BaseDailyCap { get; set; } = 1000;
        [JsonProperty("在线奖励积分(默认5)")] public int OnlineRewardPoints { get; set; } = 5;
        [JsonProperty("在线奖励间隔(分钟, 默认10)")] public int OnlineRewardInterval { get; set; } = 10;
        [JsonProperty("Boss奖励积分分配方式(按贡献分配true|平均分配false, 默认true)")] public bool BossRewardByDamage { get; set; } = true;

        // 盲盒价格映射表
        [JsonProperty("盲盒价格表 (序号: 价格)")] 
        public Dictionary<int, int> BlindBoxPrices { get; set; } = new Dictionary<int, int>
        {
            { 101, 1500 },
            { 102, 1500 },
            { 103, 2500 },
            { 104, 2500 },
            { 105, 2500 },
            { 106, 2500 },
            { 107, 750 }
        };
        
        // 用一个列表(List)来存储所有的商品配置
        [JsonProperty("道具列表")] 
        public List<ShopItem> ShopItems { get; set; } = new List<ShopItem>();
        
        /// <summary>
        /// 核心方法：负责从硬盘读取配置。如果没找到，就自动生成一个写好默认数值的新文件。
        /// </summary>
        public static EpointConfig Read(string filePath)
        {
            // 如果硬盘上不存在这个 JSON 文件（通常是第一次使用本插件时）
            if (!File.Exists(filePath))
            {
                // 使用 C# 11 的 """ 语法，可以直接定义多行且包含双引号的纯正 JSON 字符串
                string defaultJson = """
                {
                  "快节奏模式(设为true则积分获取效率及上限翻倍, 适用于进度推进快的高手玩家, 默认false)": false,
                  "每日积分获取基础上限(默认1000)": 1000,
                  "在线奖励积分(默认5)": 5,
                  "在线奖励间隔(分钟, 默认10)": 10,
                  "Boss奖励积分分配方式(按贡献分配true|平均分配false, 默认true)": true,
                  "盲盒价格表 (序号: 价格)": {
                  "101": 2000,
                  "102": 2000,
                  "103": 2000,
                  "104": 2000,
                  "105": 2000,
                  "106": 2000,
                  "107": 750
                  },
                  "道具列表": [
                    { "序号": 1, "商品名称": "生命水晶", "原版物品ID": 29, "积分价格": 500, "物品数量": 1, "是否限购": false },
                    { "序号": 2, "商品名称": "铂金币", "原版物品ID": 74, "积分价格": 1000, "物品数量": 1, "是否限购": false },
                    { "序号": 3, "商品名称": "耍蛇者长笛", "原版物品ID": 4262, "积分价格": 2000, "物品数量": 1, "是否限购": false },
                    { "序号": 4, "商品名称": "虫洞药水", "原版物品ID": 2997, "积分价格": 500, "物品数量": 5, "是否限购": false },
                    { "序号": 5, "商品名称": "强效治疗药水", "原版物品ID": 499, "积分价格": 2000, "物品数量": 10, "是否限购": false },
                    { "序号": 6, "商品名称": "超级治疗药水", "原版物品ID": 3544, "积分价格": 4000, "物品数量": 10, "是否限购": false },
                    { "序号": 7, "商品名称": "学徒诱饵", "原版物品ID": 2674, "积分价格": 300, "物品数量": 5, "是否限购": false },
                    { "序号": 8, "商品名称": "熟手诱饵", "原版物品ID": 2675, "积分价格": 700, "物品数量": 5, "是否限购": false },
                    { "序号": 9, "商品名称": "大师诱饵", "原版物品ID": 2676, "积分价格": 1500, "物品数量": 5, "是否限购": false },
                    { "序号": 10, "商品名称": "史莱姆法杖", "原版物品ID": 1309, "积分价格": 6000, "物品数量": 1, "是否限购": true },
                    { "序号": 11, "商品名称": "捣蛋猫", "原版物品ID": 5663, "积分价格": 6000, "物品数量": 1, "是否限购": true },
                    { "序号": 12, "商品名称": "火绒狐", "原版物品ID": 5664, "积分价格": 8000, "物品数量": 1, "是否限购": true },
                    { "序号": 13, "商品名称": "骨镐", "原版物品ID": 1320, "积分价格": 5000, "物品数量": 1, "是否限购": true },
                    { "序号": 14, "商品名称": "混沌传送杖", "原版物品ID": 1326, "积分价格": 10000, "物品数量": 1, "是否限购": true },
                    { "序号": 15, "商品名称": "女巫扫帚", "原版物品ID": 4444, "积分价格": 12000, "物品数量": 1, "是否限购": true }
                  ]
                }
                """;
                // 将上面写好的模板写进硬盘
                File.WriteAllText(filePath, defaultJson);
                // 立刻将这个模板反序列化并返回给内存
                return JsonConvert.DeserializeObject<EpointConfig>(defaultJson) ?? new EpointConfig();
            }

            // 如果文件已存在，尝试读取它
            try
            {
                string existingJson = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<EpointConfig>(existingJson) ?? new EpointConfig();
            }
            catch (Exception ex)
            {
                // 如果服主把 JSON 格式改错了（比如漏了逗号），这里会拦截崩溃并发出红色警告
                Console.WriteLine($"[Epoint] 配置文件错误，请检查 JSON 格式！错误信息: {ex.Message}");
                return new EpointConfig(); // 返回一个空的保护性配置，防止插件彻底死机
            }
        }
    }
}