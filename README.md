# StrikeCoin —— 自定义硬币颜色

给游戏注册 **5 种自定义硬币颜色**（每种一套硬币图）：

| 色 id | 效果 | 触发条件 |
|---|---|---|
| `ORANGE` | **这枚硬币第一次拼点胜利后，摧毁对手全部硬币** | 只有赢家触发 |
| `CYAN` | **本技能每拼点一次，就删掉自己第一枚青色硬币** | **不论输赢**，平局也算 |
| `BLACK` / `BLUE` / `WHITE` | 占位 —— 注册、出图都正常，拼点胜利只打一条日志，暂无功能特性 | — |

青色硬币是"消耗品"：技能带几枚青币，最多拼几个回合就被自己吃光，
吃光后该技能的硬币数变少、伤害段数也跟着变少。

id 写在静态数据 coin 的 `color` 字段上，大小写不敏感。全部美术**内嵌在 DLL 里**，
部署只需要一个 `StrikeCoin.dll`，插件目录不用放任何 PNG。

---

## 1. 装好插件

```powershell
.\build.ps1 -Deploy        # 先关掉游戏，否则 DLL 被占用
```

首次启动后会在 `BepInEx\config\com.limbusmods.strikecoin.cfg` 生成配置。

## 2. 把某枚硬币变成自定义色

游戏里硬币**没有字符串 id 字段**，颜色是靠 `SkillCoinData.color` 这个字符串
`Enum.Parse` 出来的（原版只认 GOLD / GREY / GREEN / PURPLE / NONE）。
插件 patch 了 `SkillCoinData.get_CoinColorColor`，让它额外认自定义字符串。

所以只需要**把技能数据里那枚硬币的 `color` 改成对应色 id**：

```json
{
  "id": 12345,
  "coinlist": [
    { "color": "GOLD",   "grade": 1, "scale": 12 },
    { "color": "ORANGE", "grade": 2, "scale": 16 },
    { "color": "BLACK",  "grade": 2, "scale": 16 }
  ]
}
```

> ⚠ **v1.1.0 改过标识**：更早用的 `"StrikeCoin"` 已经不再识别，
> 已经改好的静态数据要**改成 `"ORANGE"`**。
> 忘了改也不会崩 —— 未知 color 串会被兜底成 GOLD 并打 Error 日志（配置 `General.FallbackUnknownColor`，默认开）。

### ⚠ gaksungLevel 是每个等级单独一份 coinList

技能数据里 `skillData` 是按 `gaksungLevel`（觉醒/等级）分层的，**每层有自己的 coinList**。
游戏取某一层的硬币时会**往回找**：`SkillStaticData.GetCoins(gaksung)` 发现该层
`coinList` 为 null 或为空就递减索引去找上一层（已反编译 `0x181497DC0` 确认）。

所以：**只在真正写了 `coinList` 的那些层上加 `color`**，没写 coinList 的层会自动继承上一层的。

### 改静态数据的两条路

- **不用 Lethe / 直接改游戏静态数据文件**：原文件里整份对象是齐全的，
  只需在目标 coin 对象里**加一个 `"color": "ORANGE"` 字段**即可。
- **用 Lethe**：`BepInEx/plugins/Lethe/mods/<你的mod>/custom_limbus_data/skill/<任意名>.json`
  —— 但 **Lethe 是按 ID 整体替换，不是深合并**。
  `override_load_skill_data` 用 `dict.TryAdd(entry.ID, entry)`，而自定义文件被
  `nodeList.Insert(0, ...)`，先到先得 → 你的那份会**完全覆盖**原版条目。
  所以文件里必须是一份**完整的**技能定义，缺的字段会变成 0 / null / 空列表，技能会变残。
  根节点形如 `{"list": [ {...完整技能...} ]}`。

> 不打这个 patch 的话 `Enum.Parse` 遇到未知字符串会直接抛异常，技能一加载就炸 ——
> 所以**改数据前必须先把插件装上**。

## 3. 硬币图（内嵌，不用拷文件）

5 套图在源码 `art/` 目录里，**编译期内嵌进 DLL**，运行时从程序集清单里读：

```
art/ORANGE_0.png … ORANGE_5.png
art/BLACK_0.png  … BLACK_5.png
art/BLUE_0.png   … BLUE_5.png
art/CYAN_0.png   … CYAN_5.png
art/WHITE_0.png  … WHITE_5.png
```

命名规则：`<色名>_<COIN_UI_STATE>.png`（0=NONE 1=BACK 2=FRONT 3/4/5=破碎态）。
某个状态缺图就退回 `<色名>.png`，再没有就用代码画的该色硬币兜底。

**换图 = 替换 `art/` 里的同名文件后重新编译**（`.\build.ps1`）。插件目录下放 PNG 是**没有用的**。

启动日志会打印 `内嵌硬币图 30 张`；打成 0 张会打 Error，说明 csproj 的 `EmbeddedResource` 没生效。

**完整美术清单 / 哪些界面会跟着变 / 哪些不会** → 看 `ART.md`。

PNG 要求：**8-bit、非调色板**（RGB / RGBA / 灰 / 灰+Alpha 均可），不要隔行扫描。
原生 `ImageConversion.LoadImage` 在本作已被剥离，插件用的是从 Lethe 搬来的纯托管
`PngDecoder`（`System.IO.Compression.DeflateStream` 跑在插件的 CoreCLR 上，不受影响）。

## 4. 配置

| 段 | 键 | 默认 | 说明 |
|---|---|---|---|
| General | `CoinColorIds` | `ORANGE,BLACK,BLUE,CYAN,WHITE` | 注册的自定义色，逗号分隔（大小写不敏感）。顺序决定枚举值分配顺序，也决定美术资源名 |
| General | `CoinColorBaseValue` | `0` | 自定义色占用的 `COIN_COLOR_TYPE` 起始值，按上面顺序**连续**分配。`0`=自动（官方 max+1 起步）；非 0 = 从该值起连续分配，撞官方就整体回退自动并打 Warning |
| General | `FallbackUnknownColor` | `true` | 未知 color 串兜底成 GOLD + 打 Error，避免原版 `Enum.Parse` 抛异常炸掉技能加载 |
| Effect | `ActiveEffectIds` | `ORANGE` | 哪些色**真正执行**「拼点首次胜利 → 摧毁对手全部硬币」。没列进来的色仍然注册、出图，但只打一条占位日志 |
| Effect | `DestroyAllCoins` | `true` | 关掉只打日志，用于验证触发时机 |
| Effect | `SelfRemoveCoinIds` | `CYAN` | 哪些色执行「本技能每拼点一次 → 删掉自己第一枚该色硬币」。**不看胜负，平局也算**。和 `ActiveEffectIds` 重叠时以后者（摧毁）为准 |
| Effect | `RemoveSelfFirstCoin` | `true` | 自删效果总开关。关掉只打日志，用于验证触发时机 |
| Effect | `SelfRemoveMode` | `0` | `0`=**真删**（默认）：调用官方 `SkillModel.RemoveCoinModelFromListByIndex`，硬币从技能里彻底移除、界面消失。`1`=摧毁兜底：只置破碎标记，硬币留在列表里显示破碎态 —— 真删导致战斗 UI 槽位错乱时切到 1 |
| Effect | `TriggerMode` | `0` | `0`=本回合正在翻的那枚必须是有特效的自定义色；`1`=技能里有存活的该类硬币就触发。**只对 `ActiveEffectIds` 那套生效**，自删不看胜负不受影响 |
| Debug | `Verbose` | `false` | 每回合打印双方每枚硬币的颜色/存活/可用状态（自定义色显示色名而不是数字） |

> v1.2.0 把 `CoinColorId` → `CoinColorIds`（复数）、`CoinColorValue` → `CoinColorBaseValue`，
> 并移除了 `Art.SpriteFile`（美术前缀固定等于色名）。
> BepInEx 按「键名+默认值」绑定，**改名后旧 cfg 那几条自动失效**，新默认值直接生效，不用手工改 cfg
> （旧键留着无害，也可以自己删掉）。

**调试第一步**：把 `Verbose` 打开打一场拼点。日志形如

```
[StrikeCoin] round 0  ACharacterResult=WIN
  A: [0]GOLD -u [1]ORANGE -u [2]GREY -u
  B: [0]GOLD -u [1]PURPLE -u
```

`[序号]颜色 x=已摧毁 u=可用于拼点`。如果严格模式下一直不触发，
说明"场上第一枚硬币 = 本回合上场硬币"这个假设在本作不成立，把 `TriggerMode` 改成 `1`。

自删（CYAN）每触发一次会打一行，开 `Verbose` 时多一行剩余情况：

```
[StrikeCoin] CYAN 第 0 回合拼点结束（A 方）-> 删除本技能第 1 枚硬币，删前仍可上场=True
[StrikeCoin]   删除完成，A 方剩余硬币 2 枚，life=2
```

**`life` 应该正好等于这方还能上场的硬币数** —— 对不上就说明 parryingLife 修正那步算错了
（多半是"删前是否还可上场"判反了，会出现某方凭空少打一回合，或对着不存在的币空转）。

**图不对时看这一行**（每个 色×状态 只打一次，说明这张图到底是哪来的）：

```
[StrikeCoin] 硬币图 ORANGE 状态 2 <- 内嵌 PNG ORANGE_2.png (227x222)
```

出现 `程序化兜底（没找到内嵌图）` 说明内嵌资源没取到（多半是没重新编译）；
出现 `内嵌 PNG ...` 但游戏里还是金色的，说明**还有第三个取图出口**没接管 ——
用 IDA 查 `UISpriteDataManager.GetCoinSprite` / `CoinColor.TryGetSprite` 的 xref 找它。

---

## 5. 官方更新后的兼容性

官方将来加新硬币颜色是迟早的事，插件把这块做成了自适应：

| 官方可能的变动 | 处理 |
|---|---|
| **新增枚举成员**（`COIN_COLOR_TYPE` 多出 5、6…） | 启动时扫一遍官方已占用值，从 `max+1` 起给 5 种自定义色连续分配；分配到的值立刻占住，所以互相不会重复。**不再写死任何数值**，官方加一个我们整体往后再挪。数值只存在于运行时（静态数据里只有字符串），重算无副作用 |
| **新增的颜色正好叫 `ORANGE` / `BLACK` …** | 逐色检测并打醒目 Warning，但**仍然劫持** —— 保证模组不会因为官方更新而失效。代价是官方同名硬币也会套用本模组的硬币图 |
| **新增 `COIN_UI_STATE`**（>5） | 复用 FRONT 那张图，不会显示成"没翻过的完好币"，并提示该色遇到了未知状态 |
| **官方改了函数签名 / 方法名** | 所有 hook 按方法名查找（不是硬编码 VA），启动时会打 `hook 自检: N/N 全部命中`；缺一个就打 Error 并列出是哪个 |

启动时日志里会有这几行，升级游戏后先看它们：

```
[StrikeCoin] 内嵌硬币图 30 张。
[StrikeCoin] 官方 COIN_COLOR_TYPE 成员: GOLD=0 GREY=1 GREEN=2 PURPLE=3 NONE=4
[StrikeCoin] 已注册硬币颜色: ORANGE=5(摧毁) BLACK=6(占位) BLUE=7(占位) CYAN=8(自删) WHITE=9(占位)（自动分配）
[StrikeCoin] hook 自检: 6/6 全部命中。
```

---

## 实现要点（改动前务必读）

### 不能 patch 虚方法

IL2CPP 下 Harmony patch 虚方法，"调用原方法"会走回被 patch 的 override
→ 无限递归 → StackOverflow → 游戏闪退。
`Patches.Apply()` 里对 `IsVirtual` 做了断言，命中会打 Error 并跳过。
本插件 6 个 hook 点全部在 `dump.cs` 里核对过，均无 `Slot:` 标记。

### 每种颜色都必须占一个枚举值

图像出口 `UISpriteDataManager.GetCoinSprite(color, state)` = 全游戏硬币图像的唯一出口
（OneCoinLog / BattleUnitView / DuelActionInfo / BattleSkillViewer 都汇流到这里），
但它**只收 `(颜色数值, 状态)`，拿不到 CoinModel 实例**，没法按实例区分 ——
所以"注册一种颜色"必然等于"占一个 `COIN_COLOR_TYPE` 数值"。

### 图像出口有两个，两个都得 patch

1. `UISpriteDataManager.GetCoinSprite(color, state)` `0x1815BF8F0`（非虚）
   —— 大多数界面走这条（OneCoinLog / BattleUnitView / StartCoinToss / Start_Cointoss…）。
2. `CoinModel.CoinColor.TryGetSprite(state)` `0x1824961A0`（非虚，**14 处调用**）
   —— 投币动画、技能信息面板、EGO 选择界面等走这条。

**第 2 条最容易漏**：它的逻辑是「查实例自己的 `_sprites` 缓存，未命中就用
`this.get_type()` 去查 `UISpriteDataManager.GetCoinSprite`」。
`get_type()` 是**虚方法（Slot 4）**，IL2CPP 下不能 patch；而 `TryGetCoinColor` 的兜底实例
只能是 `CoinColor_GOLD`，所以它会一直拿 GOLD 去查 → **金色图**，还会把金色图缓存进实例。
所以必须 patch `TryGetSprite` 本身：按原生指针认出"这是我们自己造的兜底实例"，
直接返回对应的自定义图。`Patch()` 里有 `IsVirtual` 断言，万一官方把它改成虚方法会自动跳过并打 Error。

> v1.2.0 只 patch 了第 1 条 → 投币动画和技能信息面板里的自定义色显示成金色，
> 只有战斗硬币日志是对的。v1.2.1 补上第 2 条。

**每种自定义色必须各有一个兜底实例**（`FallbackColors`），不能共用 ——
`TryGetSprite` 会把图缓存进实例自己的 `_sprites`，共用一个会让五种色互相串味。

### 摧毁硬币要动两个标志位

`SetDestroyedState(true)` 只管 `_isDestroyed`。**必须同时把 `_isUsableInDuel` 置 false**，
否则它下一回合照样被选上场。

### 必须把败方 parryingLife 写 0

拼点循环终止条件只看 `actorParryingLife <= 0 || opponentParryingLife <= 0 || count >= 99`，
**不看硬币数**。不清零的话赢家会对着"没硬币的对手"空转，
`GetDestroyTargetCoin` 会走进 `LogError` 分支返回 null。

### 青色：删硬币要用官方原语，而且下标是 `_coinList` 的

删除硬币的官方做法是 `SkillModel.RemoveCoinModelFromListByIndex(idx)` `0x18126F9F0`
（public、非虚，IDA 已核），它做三件事：

```
_coinList.RemoveAt(idx)  →  返回被删的 CoinModel  →  _coinListCacheDirty = 1
```

和游戏自己 `RemoveAllTempCoins` 删临时币是同一套范式，所以不用自己拼 `RemoveAt` + 置脏标记。
（`SkillModel.DisableCoin(idx)` 只是把 `_isActive` 置 0，**不是删除**，别用错。）

两个坑：

1. **下标必须是 `_coinList` 的，不是 `CoinList` 的。**
   `get_CoinList()` `0x181257BF0` 返回的其实是过滤缓存 `_validCoinListCache`
   （会跳过 `Battle.Coin.CoinStatusManager.IsValidCoin` 判 false 的币），
   两边下标并不一一对应。所以找币要直接在 `skill._coinList` 上遍历。
2. **删完必须同步该方的 `parryingLife`。**
   `Duel` `0x180B6B3D0` 里 `actorParryingLife` 初值 = `SkillModel.GetCoinNum()` = 硬币数
   （`GetCoinNum` `0x18125D3B0` 就是 `get_CoinList().Count`），循环只看 life 归没归 0。
   删一枚 = 该方可战之币 -1，life 也得 -1，否则下一回合它会拿一枚不存在的币上场。
   **但只在被删那枚当时还 `IsUsableInDuel` 时才 -1** —— 如果它正是本回合刚被
   `CheckCoinDestroy` 打掉的那枚，life 已经为它减过一次了，再减就是"一枚币扣两条命"。

**UI 槽位不用担心**（已静态核实）：硬币槽位 UI（`CoinSlotListUI` `0x1822E9FF0` /
`CoinWithEffectSlotListUI` `0x1822EA720`）用的是预制体里的**固定槽位池**，
`SetCoinNum(n)` 只做"显示前 n 个 + 其余 `SetActive(false)`"，硬币数变少本来就是它支持的用法。
而且这两个 UI 的调用方是异想体图鉴和选人界面，**不是战斗实时显示** ——
战斗画面是按回合日志（`BattleLog_Parrying`，在 `Parrying` **之前**创建）驱动的，
删除发生在本回合画面定下之后。
唯一确定的影响是决斗结束后 `GetAliveCoins()` 变少、伤害段数跟着变少（预期行为）。

### 已知的副作用

- 自定义色非 GOLD，`IsSuperCoin` 会为 true（`grade != 1 || color != GOLD`），
  一堆 `BuffAbility_*SuperCoin*` 加成可能被触发。**这是确认要保留的行为**。
- **破坏规则目前沿用 GOLD**（`comGrade >= myGrade`），因为 `TryGetCoinColor` 的兜底实例是
  `CoinColor_GOLD`，五种色共用。等占位色定下各自特性，这里要按 def 返回不同实例。
- 技能信息面板的预览硬币**不会变色**：`BattleUI.CoinWithEffectSlotListUI.GetCoinSprite`
  硬编码 GOLD，要改得单独 patch。

### 给占位色填真实特性

1. `CoinRegistry.cs` 的 `CoinEffect` 加一个枚举值；
2. 看效果是否依赖胜负，选一条路：
   - **赢了才触发**（像 ORANGE）→ 在 `Patches.ApplyEffect()` 加一个 `case`，
     再把色 id 写进配置 `Effect.ActiveEffectIds`；
   - **不看胜负，每次拼点都触发**（像 CYAN）→ 在 `Patches.TryRemoveOwnEffectCoin()`
     那套里加分支。**不能塞进 `ApplyEffect`** —— 它在 `Postfix_Parrying` 里排在判胜负之后，
     DRAW 时会直接 return 掉。
3. 改 `CoinRegistry.Build()` 里 `EffectOf` 的判定逻辑，接上新的配置键。

新增配置项时**必须换一个新键名**：BepInEx 按「键名+默认值」绑定，
沿用旧键名的话已有 cfg 里的旧值会被保留，新默认值不会生效。

## 文件

| 文件 | 内容 |
|---|---|
| `Patches.cs` | 6 个 Harmony patch + 触发判定 + 两套效果分发（赢家摧毁 / 每次拼点自删）+ 启动时兼容性自检 |
| `CoinRegistry.cs` | 五种自定义色的定义（`CoinDef` / `CoinEffect`）+ 注册表 + 枚举值分配 + 兜底着色表 |
| `CoinColorAllocator.cs` | 扫描官方已占用的 `COIN_COLOR_TYPE`，动态分配不撞车的值 |
| `CoinArt.cs` | 内嵌 PNG 加载 + 精灵缓存 + 按色着色的兜底硬币 |
| `PngDecoder.cs` | 移植自 Lethe 的纯托管 PNG 解码器 |
| `ModConfig.cs` | BepInEx 配置 |
| `Plugin.cs` | 入口 |
| `art/` | 5 色 × 6 状态的 PNG 源图（编译期内嵌，不随 DLL 部署） |

## 构建

```powershell
.\build.ps1
```

零 NuGet 依赖，用 `--no-restore` 绕开本机 `dotnet restore` 的环境问题。
`obj/project.assets.json` 是手工生成的，**勿删**。
