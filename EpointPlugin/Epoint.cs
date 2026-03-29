using System;
using System.IO;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace EpointPlugin
{
    // =========================================================================
    // [主入口类] TShock 启动时的第一个实例化，负责全局调度
    // =========================================================================
    [ApiVersion(2, 1)] // 声明适配的 TShock API 版本
    public class Epoint : TerrariaPlugin
    {
        // 插件的基本元数据，在控制台中用 /plugins 查看
        public override string Name => "Epoint System";
        public override Version Version => new Version(1, 0, 0);
        public override string Author => "badgoose";
        public override string Description => "[EasyPoint] 一个游戏内获取积分并兑换道具的插件";

        // 全局静态实例：可以在其他文件里用 Epoint.Config 访问配置，用 Epoint.Data 读写数据
        public static EpointConfig Config { get; private set; } = null!;
        public static JsonDataManager Data { get; private set; } = null!;

        public Epoint(Main game) : base(game)
        {
        }

        /// <summary>
        /// 当服务器启动并开始加载插件时执行
        /// </summary>
        public override void Initialize()
        {
            // 1. 寻找/创建 tshock 目录下的 epoint 专属文件夹
            string baseDir = Path.Combine(TShock.SavePath, "epoint");
            if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);

            // 2. 将控制权交给 EpointConfig 去读写 JSON 配置文件
            string configPath = Path.Combine(baseDir, "epointconfig.json");
            Config = EpointConfig.Read(configPath);

            // 3. 将控制权交给 JsonDataManager 去读写玩家数据文件
            try
            {
                Data = new JsonDataManager(baseDir);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Epoint] 数据中心初始化错误: {ex.Message}");
                Console.ResetColor();
                return; // 若无法读写，立刻终止加载
            }

            // 4. 注册玩家指令到 TShock 指令大厅，并绑定对应的业务方法
            Commands.ChatCommands.Add(new Command("epoint.admin", ReloadConfig, "epreload"));
            Commands.ChatCommands.Add(new Command("", EpointSystem.EpHelpCommand, "ephelp"));
            Commands.ChatCommands.Add(new Command("", EpointSystem.EpInfoCommand, "epinfo"));
            Commands.ChatCommands.Add(new Command("", EpointSystem.EpRankCommand, "eprank"));
            Commands.ChatCommands.Add(new Command("", EpointSystem.EpShopCommand, "epshop"));
            Commands.ChatCommands.Add(new Command("", EpointSystem.EpBuyCommand, "epbuy"));

            // 5. 把业务逻辑里的处理方法挂载到泰拉瑞亚原版的“发生事件”上
            PlayerHooks.PlayerPostLogin += EpointSystem.OnPlayerLogin; 
            ServerApi.Hooks.ServerLeave.Register(this, EpointSystem.OnPlayerLeave); 
            GetDataHandlers.NPCStrike += EpointSystem.OnNpcStrike; 
            ServerApi.Hooks.NpcKilled.Register(this, EpointSystem.OnNpcKilled); 
            
            // 6. 激活时钟
            EpointSystem.InitializeTimer();
        }

        /// <summary>
        /// 当输入 /epreload 时执行：将修改过的内容刷新进内存
        /// </summary>
        private void ReloadConfig(CommandArgs args)
        {
            try
            {
                string baseDir = Path.Combine(TShock.SavePath, "epoint");
                string configPath = Path.Combine(baseDir, "epointconfig.json");
                Config = EpointConfig.Read(configPath);
                
                EpointSystem.ReloadTimer(); // 刷新定时器的时间
                
                args.Player.SendSuccessMessage($"[c/00FF00:[Epoint] 配置文件已重新加载！]");
            }
            catch (Exception ex)
            {
                args.Player.SendErrorMessage($"[Epoint] 重新加载失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 当服务器关闭或插件被卸载时执行
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 撤回挂在事件上的业务，否则重载插件时会导致重复执行
                PlayerHooks.PlayerPostLogin -= EpointSystem.OnPlayerLogin;
                ServerApi.Hooks.ServerLeave.Deregister(this, EpointSystem.OnPlayerLeave);
                GetDataHandlers.NPCStrike -= EpointSystem.OnNpcStrike;
                ServerApi.Hooks.NpcKilled.Deregister(this, EpointSystem.OnNpcKilled);
                
                // 掐断时钟
                EpointSystem.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}