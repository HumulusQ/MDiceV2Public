# 权限预载重构说明（摘要）

本文档简要说明我为将权限信息预载到消息对象（`Msg`）并让各指令使用缓存权限所做的更改，以及对 `.as`（代投）指令权限继承的实现细节。

## 背景
当前代码在各处直接通过全局缓存（如 `personAuth`、`groupAuth` 等）检查权限，导致：
- 指令处理器在每次检查权限时重复读取共享缓存；
- 代投（`.as`）会创建以目标用户为 `UserId` 的新 `Msg`，从而在后续权限判断中以目标用户身份进行检查（存在安全风险）。

## 主要改动（概述）
- 在 `MDiceV2.Core/Models/Msg.cs` 中新增预载权限字段：
  - `IsAuthInfoLoaded`、`UserAuthLevel`、`IsSystemAccount`、`IsMasterAccount`、`HasAuthPermission`、`IsWhitelisted`、等。
- 在 `MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs` 中：
  - 在 `OnHandleMessage` 起始处调用 `EnsureMsgAuthInfo(msg)`，将调用者的权限状态预先计算并写入 `msg`；
  - 新增 `EnsureMsgAuthInfo(Msg msg)` 实现，从 `basicConfigData.Master`、`personAuth` 等缓存读取并计算权限后写回 `msg`；
  - 将原先对系统/主控权限的判断改为使用 `msg.IsSystemAccount` / `msg.IsMasterAccount` / `msg.HasAuthPermission` 等缓存字段；
  - 在 `.as` 创建的委托消息上显式复制调用者的权限缓存字段（让被代投的 `Msg` 继承发起者权限）；
  - 移除/替换了原有的基于 `userId` 的冗余权限检查函数（简化逻辑，避免重复检查）。

## 对 `.as` 的影响
- `HandleAsCommand` 在为每个目标构造新的 `Msg` 时，会拷贝原始 `msg` 的权限缓存字段（`UserAuthLevel`、`HasAuthPermission` 等），因此被代投的处理流程在做权限判断时会使用发出者的权限，而不是目标用户的权限，满足“代投继承操作者权限”的要求。

## 好处
- 权限判定统一且更高效（避免同一消息多次查表）；
- 降低代投被滥用来以目标身份获得更高权限的风险；
- 后续若需扩展额外权限字段（如群内管理员角色缓存），可在 `EnsureMsgAuthInfo` 中统一填充并供所有指令使用。

## 后续建议
- 如果需要缓存“群内管理员/群主”信息到 `Msg`（即用户在某群是否为管理员），我可以：
  - 在 `EnsureMsgAuthInfo` 中增加对 `MessageDistribution` 的异步角色检查（并缓存结果）；或
  - 订阅 `MessageDistribution.OnGroupAdmin` 事件，在管理员变更时更新一个本地缓存并由 `EnsureMsgAuthInfo` 读取。

## 修改过的文件
- MDiceV2.Core/Models/Msg.cs
- MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs

---
如果你希望我将群管理员角色也预载到 `Msg`（并在代投中继承），或帮你运行一次构建以验证改动，我可以继续处理。