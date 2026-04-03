# 💰 Epoint Plugin | 一个简单的积分商店插件

([前往Tshock主页](https://github.com/Pryaxis/TShock)) | ([前往TShock中文插件库](https://docs.terraria.ink/zh/)) | ([前往Tshock插件搜集仓库](https://github.com/UnrealMultiple/TShockPlugin))

$\color{red}\textbf{>这是我尝试制作的第一个插件，尚不成熟，且可能含有潜在bug。如果您有什么好的建议或想法，欢迎提出！！}$

## 📖 插件简介

基于 **Tshock** 的 **Tererria** 服务器积分系统插件，核心功能是获取积分和购买道具。
无论是前期爆肝开荒，还是后期休闲养老，Epoint Plugin 旨在为游玩过程增添趣味与降低难度。

---

## 指令列表

| 指令 | 说明 |
|------|------|
| `/ephelp` | 显示帮助菜单 |
| `/epinfo` | 查看个人积分档案 |
| `/eprank` | 查看积分排行榜 |
| `/epshop [页码]` | 打开商店 |
| `/epbuy <商品序号> [次数]` | 购买商品 |

---

## 💡 功能模块

### 1. 每日签到
- 玩家每日首次登录或执行 `/epinfo` 时自动触发
- 奖励公式：`基数 × 连续签到系数 × 运气系数`
- 设有单日积分获取上限防止数值膨胀，默认首日获取上限为 1000 ep

### 2. 在线奖励
- 每累计在线 10min（默认） 发放在线奖励
- 默认基础奖励 10 ep，有概率暴击
- 极低概率发放灵韵积分（有保底机制）

### 3. Boss 击杀奖励
- 标准 Boss：通过 `On.Terraria.GameContent.BossDamageTracker` 钩子获取原版伤害统计
- 非标准 Boss（例如世吞）：自定义伤害追踪算法
- 奖励公式：`基数 × 多人倍率 × 输出占比`
- 奖励基数：预定义 `BossBasePoolMultipliers` 映射表
- 多人倍率：1人=1.0，2人=1.6，3人=2.1，4人=2.4，≥5人=2.5

### 4. 小怪里程碑奖励
- 击杀同种怪物每满 50 只触发一次
- 基础奖励范围 50~150 ep（按怪物最大生命值指数衰减曲线计算）

### 5. 积分商店
- **普通商店**：商品支持配置文件自定义
- **盲盒商店**：击败血肉墙后解锁
- **神秘商店**：初始隐藏，累计获得10 cp后解锁，商品支持配置文件自定义
- 商品展示分页（/epshop <页码>）
- 包含普通商品、限购商品、盲盒商品、稀有商品
- 预定义掉落池（BoxDrop），开启盲盒时全服广播掉落结果
- 购买时自动校验：背包空间、积分余额、限购次数

### 6. 幸运折扣机制
- 每次购买商品皆有概率触发幸运折扣，若连续多次未触发则会增加触发概率
- 触发折扣后商品价格打 7 折（默认），折扣状态仅限下一次购买

### 7. 会员等级
- 根据累计消费和全商品购买情况自动晋升
- 初级会员：全场商品 95 折（神秘商店除外）
- 中级会员：全场商品 9 折（神秘商店除外）
- 高级会员：全场商品 85 折（神秘商店除外）
- 至尊会员：全场商品 95 折（神秘商店除外，需玩家购买过全部商品解锁）
---

## ⚙️ 配置文件

首次加载插件后，会在 `tshock/epoint/` 目录下自动生成 `epointconfig.json` 文件。支持热修改并使用 `/epreload` 实时生效

| 字段 | 类型 | 说明 |
|------|------|------|
| `BaseDailyCap` | int | 基础每日积分上限 |
| `FastPacedMode` | bool | 快速模式（积分上限及奖励翻倍） |
| `OnlineRewardInterval` | int | 在线奖励间隔（分钟） |
| `ShopItems` | array | 普通商店商品定义 |
| `MysticShopItems` | array | 神秘商店商品定义 |
| `BlindBoxPrices` | array | 盲盒价格修改 |

---

## 💾 玩家数据存储

- 路径：`tshock/epoint/players/<玩家名>.json`
- 服主可随时查阅或修改玩家的积分与限购记录
- 每玩家独立文件，支持热重载
- 定时批量刷写，玩家退出时同步

---

## 技术实现

### 数据并发安全
- 所有玩家数据读写使用 `lock(data.SyncLock)` 保护
- 使用 `ConcurrentDictionary` 存储临时追踪数据（在线时长、击杀计数、伤害统计等）
- 批量购买时一次性锁住数据，避免多次扣款竞态

### 定时器
- `_onlineTimer`：按配置间隔触发在线奖励
- `_autoSignInTimer`：每分钟执行自动签到 + 清理死亡Boss追踪 + 数据刷写

### 事件钩子
- `PlayerPostLoginEventArgs`：玩家登录时重置在线计数、触发签到
- `LeaveEventArgs`：玩家离开时清理缓存、刷写数据
- `GetDataHandlers.NPCStrikeEventArgs`：记录Boss伤害
- `NpcKilledEventArgs`：处理非标准Boss结算和小怪里程碑
- `BossDamageTracker.OnBossKilled`：处理标准Boss结算

### 数据持久化
- 路径：`tshock/epoint/players/{账号名}.json`
- 每玩家独立文件，支持热重载
- `FlushAll()` 定时批量刷写，`FlushAndRemove()` 玩家退出时同步

---

## 注意事项

1. **世界吞噬怪**目前采用特殊处理（body 分段合并），击杀判断依赖所有体节死亡
2. 雕像生成的怪物不计入里程碑
3. 盲盒掉落概率总和可不严格归一化，按列表顺序累加匹配
4. 在线奖励的暴击保底计数器在玩家退出时清空
5. 部分功能后续可能会修改

-----

## 📦 安装指南

1.  下载最新版本的 `EpointPlugin.dll` 文件（`EpointPlugin.pdb` 文件可选）
2.  将该 `.dll` （`.pdb` 可选）文件放入泰拉瑞亚服务器的 `ServerPlugins` 文件夹中
3.  重启服务器，或使用 `/reload` 指令
4.  进入服务器，输入 `/ephelp` 验证是否加载成功

-----

Made with ❤️ by badgoose for the Terraria Server Community.