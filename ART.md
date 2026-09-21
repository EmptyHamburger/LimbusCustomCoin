# StrikeCoin 美术资源清单

## 一、要做几张图

**一种颜色的全部美术 = 1 张精灵图 × 6 个状态**（5 种自定义色就是 5 × 6 = 30 张），就这些。

反编译确认 `CoinSpriteData` 只有三个字段（`dump.cs:636391`）：

```csharp
public struct CoinSpriteData
{
    public COIN_COLOR_TYPE coinColorType;
    public COIN_UI_STATE   uiState;
    public Sprite          sprite;
}
```

也就是说**颜色 × 状态 → 一张图**，没有单独的边框、光效、特效资源挂在硬币颜色上。
（`UISpriteDataManager` 里那些 `_attributeFrameSpriteList` / `_egoFrame*` 都是技能框和 EGO 用的，
与硬币颜色无关。）

### 六个状态

| 值 | 枚举名 | 含义 | 什么时候出现 |
|---|---|---|---|
| 0 | `NONE` | 未确定 | 还没掷的时候 |
| 1 | `BACK` | 背面 | 掷出反面（判定方式见下） |
| 2 | `FRONT` | 正面 | 掷出正面（命中） |
| 3 | `BROKEN_NONE` | 破碎-未确定 | 已被打碎但还没掷 |
| 4 | `BROKEN_BACK` | 破碎-背面 | 打碎后掷出反面 |
| 5 | `BROKEN_FRONT` | 破碎-正面 | 打碎后掷出正面 |

正反面的判定（`CoinWithEffectSlotListUI.GetCoinState` `0x1822EB830`）：

```csharp
return isHead ? COIN_UI_STATE.FRONT : COIN_UI_STATE.BACK;   // 1 或 2
```

## 二、文件命名

美术**不放在插件目录**，而是放在源码 `art/` 下，**编译期内嵌进 DLL**
（csproj 的 `EmbeddedResource`，资源名 `StrikeCoin.Art.<文件名>`）。
插件目录下放 PNG 是无效的。

五色各一套，文件名 = `<色名>_<状态>.png`：

| 文件名 | 覆盖的状态 |
|---|---|
| `ORANGE_0.png` / `BLACK_0.png` / `BLUE_0.png` / `CYAN_0.png` / `WHITE_0.png` | NONE |
| `*_1.png` | BACK（背面） |
| `*_2.png` | FRONT（正面）**← 最重要** |
| `*_3.png` | BROKEN_NONE |
| `*_4.png` | BROKEN_BACK |
| `*_5.png` | BROKEN_FRONT **← 第二重要** |
| `ORANGE.png` 等 | 兜底，一张图吃所有状态 |

查找顺序：**先找内嵌的 `<色名>_<状态>.png`，找不到就用 `<色名>.png`，再找不到就用代码画的该色硬币**
（主色按 `CoinRegistry` 里的着色表：橘 / 深灰 / 蓝 / 青 / 白）。

所以最省事的起步路径：**只做一张 `<色名>.png`**，六个状态全用它，跑通了再细分。
换图 = 替换 `art/` 里的同名文件后 `.\build.ps1` 重新编译。

启动日志会打印 `内嵌硬币图 30 张`；打成 0 张说明 `EmbeddedResource` 没生效。

## 三、规格

- **尺寸**：任意。插件会读原版金色硬币的 `rect` / `pivot` / `pixelsPerUnit`，
  按你的图宽等比换算 pixelsPerUnit，**保证显示尺寸和原版金色硬币完全一致**。
  想省事就照原版尺寸出 —— 插件首次加载时会把原版参数打进日志，找这一行：

  ```
  [StrikeCoin] 原版金色硬币参考参数: rect=128x128 pixelsPerUnit=100 pivot=(64.0, 64.0)
  ```

- **格式**：8-bit PNG，RGB 或 RGBA 都行（灰/灰+Alpha 也支持）。
  **不要调色板型、不要隔行扫描**（解码器限制，与 Lethe 一致）。透明背景请用 RGBA。
- **朝向**：PNG 是左上原点，Unity 是左下原点 —— 解码器**已经做了 Y 翻转**，你按正常方向出图即可。

## 四、改完后会跟着变的地方

所有走 `UISpriteDataManager.GetCoinSprite` / `CoinModel.GetCoinSprite` 的界面都会自动生效：

| 界面 | 入口 |
|---|---|
| 战斗硬币日志 | `OneCoinLog.SetCoinHead` `0x180CBC310` → `get_CoinSprite` `0x180CBB430` |
| 拼点投币动画 | `BattleDuelViewer.DuelActionInfo.StartCoinToss` `0x182766320` |
| 技能投币动画 | `BattleSkillViewer.Start_Cointoss` `0x182E7D050` |
| 单位信息面板-技能内容 | `UnitInformationSkillContent.SetCoinUIActive` `0x180CA5930` |
| 单位信息面板-技能槽 | `UnitInformationSkillSlot.SetActiveCoinUI` `0x180CCAD50` |
| 单位信息-更新后技能框 | `UnitInformationUpdatedSkillBox.SetData` `0x18233FFA0` |
| EGO 选择界面技能信息 | `BattleUI.EGOSelectUISkillInfo.SetData` `0x18229D6F0` |
| 选目标时技能数值详情 | `BattleUI.TargettingSkillInfo_Base.SetSkillValueDetailUI` `0x1822E52E0` |

## 五、不会跟着变的地方

**战斗中的技能槽硬币预览**（技能按钮下面那一排小硬币）保持金色。

原因不是我们没 patch，而是**游戏数据里就没有颜色信息**：
它走 `BattleUI.CoinWithEffectSlotListUI.GetCoinSprite` `0x1822EB7D0`，
函数体就一行 —— `UISpriteDataManager.GetCoinSprite(COIN_COLOR_TYPE.GOLD, state)`，写死 GOLD。
而它拿到的 `OneCoinResult` 结构体（`dump.cs:17153`）里只有
`prob / idx / scale / isHead / operatorType ...`，**根本没有颜色字段**，想改也无从判断。

要强行改成橘色只能 patch 那个 private 方法无条件替换，但那样**所有**技能的技能槽硬币都会变橘色，
不建议。

## 六、不需要改的

- **硬币上的罗马数字**（Ⅰ / Ⅱ / Ⅲ…）：`UISpriteDataManager._coinRomeSpList`，
  按硬币序号取，**与颜色无关**，全游戏共用，不要动。
- **正反面的底色/命中色**：`CoinEffectConfig._successColor` / `_failColor`，
  按 `OPERATOR_TYPE`（加减乘）和状态取，不是按颜色。
- **技能框 / 属性框 / EGO 框**：与硬币颜色无关。
