# VRC-00B 数据与共享接口合同

版本：1.0首批基线。日期：2026-11-03。依据：[实施规划](../visual-room-composer实施规划.md)第3—7节、[分批指引](../visual-room-composer分批部署指引.md)00B/01B/01C。中立IR、矩阵/几何、CPU租约、最小文件框接口及CoreCLR路径已形成01的冻结基线，实际证据见[批次日志](../batch_logs/VRC-01-引擎与FBX可行性.md)。其余文档、历史、库与生产作业合同仍须由对应批次实现。

本文将已采纳的工程建议具体化，不改变Windows独立程序、三格式运行时输入和FBX运行时输出的明确范围。0.25米网格、单层、四向操作、起始预算和7400 Binary输出仍是工程起始方案。尚未落地的文档、历史、库、作业、存储和导出服务以“待实施”标记。

## 1. 程序集与所有权

| 模块 | 允许引用 | 责任与禁止事项 |
| --- | --- | --- |
| Core | 标准运行库 | 不可变CPU模型、数学、文档合同、几何、资源句柄；asmdef的noEngineReferences=true |
| Formats | Core、已核实可用的标准运行库 | 格式词法、语义、规范化、writer；不引用UnityEngine、UnityEditor、ShoppingDash或模型SDK |
| Application | Core、消费者使用的中立接口 | 命令、会话、作业状态、文档/库事务协调；不依赖具体Infrastructure或Presentation实现 |
| Infrastructure | Core、Application的接口 | 文件快照、依赖解析、原子存储、平台文件框；只在允许根内处理路径 |
| Presentation | Core、Application、Unity API | UI Toolkit、模型realization、视口、材质与Unity对象租约 |
| Bootstrap | 上述实现 | 装配根、平台启动；不承载文档规则 |

Core集合在构造时复制，向消费者提供只读视图；向量与矩阵为值类型。输入数组随后被修改不会改变既有ModelScene。纹理数据为独立副本并计算SHA256；SourceUri仅是来源记录，不能触发隐式读取。只读CPU数据可供后台线程访问；Unity对象创建、发布与销毁均由Presentation投递主线程。

## 2. 版本和身份

所有ID按Ordinal比较，不能用显示名称、列表下标或本地化文本作为身份。项目、房间、组、放置实例、定义和仓库使用新建稳定ID；模型节点ID由源稳定对象身份和导入命名域确定。两个相同名称的节点合法，两个相同ID的节点不合法。

| 字段 | 含义 |
| --- | --- |
| format / schemaVersion | 文件类型及数据布局版本；工程初始为visual-room-composer.project / 1 |
| projectId / stateId | 工程持久身份 / 当前逻辑状态身份；保存点引用状态身份，不以dirty布尔猜测 |
| revision | 工程已提交命令的单调递增序号；撤销/重做也是新的提交序号，不能让迟到任务重新匹配旧revision |
| documentGeneration | 会话代次，每次打开/关闭/替换文档变化；与磁盘revision分离，不写成内容版本 |
| definitionId / revision / contentHash | 不可变库定义的固定三元组；同ID同revision异hash是冲突，不自动择新 |
| importerVersion / importOptionsDigest | 格式语义实现及所有改变几何/材质的显式选项 |
| ModelScene.SchemaVersion / cacheSchemaVersion | 中间数据布局 / 可丢弃缓存布局；不与工程schema混用 |
| PlacementTransformResolver.RuleVersion | 当前vrc-placement-1；导出快照固定此值 |
| ModelGeometry.BakeRuleVersion | 当前vrc-affine-bake-1；烘焙缓存必须纳入键 |

内容hash为小写SHA256十六进制。工程文件hash用于外部写入冲突，不能替代逻辑状态身份。可重建CPU缓存键至少包含sourceHash、dependencyDigest、importOptionsDigest、importerVersion、cacheSchemaVersion以及受影响的规范化/烘焙规则版本。

## 3. RoomDocument与库草案（待VRC-02实施）

| 记录 | 必需字段及约束 |
| --- | --- |
| RoomDocument | format、schemaVersion、projectId、revision、stateId、metadata、unit="meter"、rooms、groups、placements、pinnedDependencies、editorSettings |
| Room | roomId、name、sharedStructurePlacementIds；主成员由Placement.roomId决定，不维护两个独立权威成员表 |
| Group | groupId、name、parentGroupId可空、roomId可空；只作逻辑组织，不保存position/rotation/scale，不为子节点增加变换 |
| Placement | placementId、roomId可空、groupId可空、definitionRef、positionGrid整数XYZ、quarterTurns=0..3、scale正XYZ、pivotOffset米制XZ、attachment可空、material/lightOverrides、hiddenInEditor、includeInExport |
| DefinitionRef | definitionId、revision、contentHash；解析到固定LibraryDefinition，不允许“latest”隐式引用 |
| LibraryDefinition | definitionId、revision、contentHash、name、category、modelReference可空、meshTransform、hitbox、floorMask、wallTopology/slots、placementPolicy、previewLights |
| AssetReference | assetId、revision、contentHash、mode=Copy/Link、sourceProvenance、relativeDependencies及每项hash、sourceCoordinates、importOptions、importerVersion、cacheSchemaVersion |
| LibraryDraft | draftId、repositoryId、baseDefinitionRef可空、可编辑定义快照、独立stateId/historyId/savepoint；不是已发布版本 |
| RepositoryRegistration | repositoryId、本地目录、状态；移除登记不删除任何文件 |

工程中的placements为唯一权威物理实例表。放置实例可有一个主roomId和一个groupId；组的roomId存在时必须与实例主归属一致。组父链禁止环与悬空；删除组只改变组织关系，删除实例须经单独命令，不按名称级联。房间可以暂为空；未归属对象在全工程中仍参与搭建和导出。

共享墙由其他Room.sharedStructurePlacementIds显式引用现有同一placementId，不能复制墙实体或网格；只能引用具有相应结构类别的实例。主归属和共享列表重复引用在导出时按placementId去重。

attachment至少包含hostPlacementId、slotId和显式局部附着参数。宿主必须存在、槽位属于固定定义，不能自附着或形成附件环。宿主移动/删除/撤销要原子带动附件；单独选中附件的导出行为见生命周期合同。

类别使用Floor、Wall、WallAttachment、Prop、Light。地板/墙以Y=0为结构诊断基准，Prop贴地、悬挂投影支撑和墙饰宿主规则保留。开放房间、交叠和支撑不完整属于可保存的作者诊断；坏引用、非有限数和非法索引属于不可接受的数据。普通家具实例scale保持(1,1,1)，可在定义中调比例；墙实例允许正XYZ，镜像来自模型层级。正式结构编辑校验在02B实现，01B数学服务不替代它。

默认复制完整依赖。Link必须显式选择并锁hash；重定位须匹配已固定hash，升级须创建新不可变版本。旧源工程和旧Profile只读。库发布不直接改当前工程引用；引用升级为另一笔可撤销工程命令。

## 4. ModelScene已落地接口

实际命名空间为VisualRoomComposer.Core，源码位于[Runtime/Core](../../VisualRoomComposer/Runtime/Core)。以下是已有类，不代表三格式服务或Player门槛已完成。

| 类型 | 数据 |
| --- | --- |
| ModelScene | Id、Nodes、Meshes、Materials、Textures、SourceCoordinates、CoordinateSpace、Diagnostics |
| ModelNode | Id、Name、ParentId可空、LocalTransform完整Matrix4d、MeshId可空、MaterialIds按槽绑定、GeometryTransformBaked |
| ModelMesh | Id、Name、Positions、Normals、Tangents、Uv0、Uv1、Colors、Submeshes |
| ModelSubmesh | MaterialSlot、TriangleIndices；-1表示未指定材质，非负槽位必须被对应实例绑定 |
| ModelMaterial | BaseColor、Metallic、Roughness、Emissive、AlphaMode/Cutoff、DoubleSided、NormalScale、OcclusionStrength与五类TextureBinding |
| ModelTextureBinding | TextureId、TexCoord=0/1、ColorSpace、WrapU/V、Min/MagFilter、Offset、Scale、RotationRadians、Channels |
| ModelTexture | Id、Name、MimeType、SourceUri、ContentHash、EncodedBytes只读、CopyEncodedBytes返回副本 |
| ModelDiagnostic | Code、Severity、Stage、Message、Source、ByteOffset可空、NodeId可空、SuggestedAction |

ModelMesh使用统一顶点索引：UV接缝、硬法线和独立源属性索引由Reader展开。可选属性长度为空或等于顶点数；索引为完整三角形。材质槽归属ModelNode实例，同一ModelMesh可以在两个节点绑定不同材质。纹理字节是编码后的原始PNG/JPEG等，不包含Unity Texture；颜色空间与采样位于使用处，同一图片可以以不同角色使用。

ModelSceneValidator.ValidateOrThrow检查重复/缺失ID、图环、深度、有限/可逆矩阵、属性长度、坏索引、退化三角形、材质槽/贴图/UV引用及计数预算。该层是解析后的语义校验，不能替代Reader分配前的字节/解压/字符串/属性预算。源文件不支持的骨骼/形变等能力必须先在Formats拒绝，不能丢掉后让IR验证通过。

## 5. 坐标、矩阵与共享放置

规范空间固定为Unity米制、Y-up、左手坐标；1单位=1米。Matrix4d采用列向量，p'=M*p；translation在M14/M24/M34；world=parentWorld*local。默认struct矩阵是零矩阵，必须显式使用Matrix4d.Identity。affine最后一行必须为(0,0,0,1)，透视矩阵不属于模型数据。

SourceCoordinateSystem记录源X/Y/Z基向量在规范空间中的方向及MetersPerUnit，生成C。ModelScene.CoordinateSpace=Source时调用ModelSceneNormalizer.ToCanonical：顶点乘C、节点C*M*C^-1、法线乘线性C逆转置；Canonical不可再次规范化。源单位/基仍保留为来源信息，不再驱动后续自动换轴。

几何绕序按代数cross(v1-v0,v2-v0)与面法线方向一致的合同处理。det(C)<0时交换每个三角形后两个索引，切线W乘det符号；法线仍仅逆转置归一化。SourceFrontFaceClockwise是源格式观察约定的元数据，不是额外翻转开关；不能因RH CCW转LH CW再重复翻一次。独立消费者继续验证此约定，01C失败时一起修订Reader、Realizer、Writer及金样。

所有仿射均保留double。法线使用逆转置再归一化；切线用正向线性矩阵，再与新法线正交化，并保留镜像handedness。非有限数、零尺度/奇异矩阵和数值不安全的近奇异矩阵拒绝；当前逆矩阵以最大线性系数归一化，det阈值1e-12，避免把合法均匀微小单位比例按绝对det误拒绝。该数值门槛属于明确实现限制，不能默默降级为identity。

共享放置矩阵按以下顺序求值，保持旧MeshTileFactory与ContentGeometry的实际行为：

```text
T = Translation(grid * 0.25 + (pivotOffset.x, GroundPivotOffset, pivotOffset.z))
P = T * RotationYQuarterTurns(quarterTurns) * Scale(instanceScale)
World = P * definition.meshTransform * modelNodeWorld
GroundPivotOffset = hitbox.size.y / 2 - hitbox.center.y  （仅Ground策略）
```

pivotOffset为水平米制世界补偿，不随yaw旋转；pivotOffset.y必须为0，作者高度来自grid.y。贴地偏移位于实例比例之外，沿用源行为；普通Prop实例不提供额外缩放。四向矩阵使用精确0/1，连续四转不靠三角函数累计误差。导入任意静态旋转与剪切在modelNodeWorld中，不扩大作者手柄的旋转范围。

01B提供ModelGeometry.BakeSceneToWorld作为共同无损基线：先求所有原始世界矩阵，将完整矩阵烘焙到各实例几何；保留名称和父子关系，所有节点local设为identity并标GeometryTransformBaked。子节点用原始world单独烘焙，不能继承父几何变换第二次。预览与writer消费同一结果，不各做有损TRS分解。烘焙前预估展开网格/顶点/三角形预算，超限直接拒绝；当前完整烘焙路径会为各实例生成独立几何，后续普通TRS共享优化需保留此基线金样，不能将此探针当作5000实例性能证明。

## 6. 租约已落地边界

ImportedSceneLease.Create(scene, onLastRelease)创建一个所有者句柄；Retain返回独立句柄；每个所有者Dispose释放自己的持有，幂等。最后一个句柄释放时清除强引用并调用一次回调；已Dispose句柄不能重新Retain或读取Scene。回调不在锁内调用；Unity适配应将销毁投递主线程，不能从任意线程直接Destroy。

文档、预览、历史记录和导出快照分别持有句柄，撤销当前逻辑引用不等于可回收历史仍固定的版本。返回的ModelScene本身不可变；调用者须在使用期间保有活句柄，不能跨Dispose继续依赖GPU或文件锁等外部资源。CPU租约骨架已实现，Unity资源缓存、解码预算、主线程销毁队列和关闭顺序仍在01A/03A/03E验证与实现。

## 7. 原创金样和验证边界

AffineProbeFixture.Create建立原创非对称模型：6顶点、2三角形、2材质、1共享源网格，含UV0/UV1、颜色、切线；root下有shear、mirror及shear下nested节点。预期坐标直接按独立公式写成常量，不由待测resolver生成。

```text
shear world: x=3.5+2*x+y, y=2.5+y, z=-1.75+3*z
mirror world: x=-1-2*x, y=2+2*y, z=2+1.5*z
nested local origin world: (3.5, 2.5, 4.25)
```

[CoreChecks](../../../Tools/VRC/CoreChecks/README.md)是实际独立入口。它覆盖原始期望、剪切/镜像法线与绕序、单位不重复、非法图/属性、不可变快照、租约释放和取消。该入口的托管结果不能代替Unity编译、Player预览、实际FBX、Blender或Unity原生导入结果。01B最终通过仍要求主控串行的目标Player对照；01C前合同状态保持draft。
