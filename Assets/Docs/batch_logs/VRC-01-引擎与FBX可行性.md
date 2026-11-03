# VRC-01 引擎与FBX可行性

日期：2026-11-03。当前状态：通过。01A/01B/01C在本日志所列探针范围内全部关闭；上游设计输入见VRC-00基线、00B合同和00C原创夹具。完整产品与03/05的格式目标没有因此提前关闭。

## 实际实现

- 01A：PanelRenderer/UXML/USS环境壳，系统中英文字体、Input System、RenderTexture、CoreCLR后台Task/JSON/压缩和Windows原生文件选择/保存。文件框使用显式Unicode非托管缓冲区；对话框取消、异常和路径选择均保留明确终态，恢复进程工作目录。
- 01B：中立不可变ModelScene、double完整仿射、源坐标规范化、共享放置变换、法线逆转置/镜像绕序/切线、预算和图校验、完整世界几何烘焙、CPU引用计数租约；Unity预览与未来导出共用CPU变换。新增129层自定义预算贯通检查。
- 01C：有界FBX ASCII/Binary7400/7500读取及7400 Binary writer；五个原创样本含非对称几何、双材质/纹理、非均匀父级、镜像、非零pivot/pre/post/offset。生产完整支持仍属于03/05。

Core和Formats的asmdef均不引用UnityEngine；Windows接口、Unity原生资源和装配根分别位于Infrastructure、Presentation、Bootstrap。未复制旧项目manifest或引入TriLib/模型SDK/运行时外部转码器。

## 已观察的验证与修复

1. 首次环境编译发现字体API命名和压缩枚举歧义，已修正；后续编译无错误/警告，完整响应留Validation。
2. 首个隐藏窗口探针等待帧结束未退出，保留日志并强制结束该专属进程；此运行不算正常退出通过。改为后台运行并使用普通帧调度；真实图形验收使用可见独立窗口。
3. 第二次Player暴露字形尚未加入动态字体和纯色几何像素判定错误；修正后中英文字形、RT、拾取坐标、几何属性及资源销毁均已有实际Player证据。
4. CoreCLR拒绝在OPENFILENAME结构中封送StringBuilder字段，改为明确IntPtr和Unicode缓冲区、finally释放；随后原生打开/保存取消及所有者窗口检查通过。自动化文件名选择改为定位实际Edit、写入后回读再接受；05版实际中文/空格/长路径选择均通过，不以静态编译替代。
5. Blender可读而Unity原生导入为空：保留三轮结构隔离与原始Blender对照。根因为FBX FileId/CreationTime/footer配对要求；writer采用独立观察并验证的固定协议数据组，自有Creator不变。固定1970元数据不代表真实导出时间；后续export-report记录真实时间。
6. 04版Player为37/39；两项失败为上述路径自动化。该Player的五个FBX读写、15/15 guards、五次Unity装配/材质纹理释放均已执行；输出迁出后Blender和Unity原生分别5/5通过。这些局部结果不将01A或主批次提前标为通过。
7. 最终05版Player39/39、正常退出0；包含全部上述文件框、字体/RT/输入、仿射世界点/属性、五个FBX读写/装配及mesh/material/texture释放检查。guards15/15。该版本新生成的五个输出再次迁出，Blender和Unity原生分别5/5；逐面拓扑/材质/角UV/法线对照均通过，法线门槛0.1°，位置门槛1e-4m。Blender最大位置误差3.04e-7m、法线误差0.0261°以内。未将不同版本专项相加冒充一次全量运行。

## 证据入口

| 范围 | 记录 |
| --- | --- |
| 目标/源基线 | `Validation/VRC-00/20261103-122155/baseline.json`；源Guidebook41份复核未变化 |
| Core纯数据 | `Validation/VRC-01ABC/core-final/result.json`，.NET10.0.14，12/12；不是Unity测试框架或Player数量 |
| 编译/构建原始响应 | `Validation/VRC-01A/20261103-env-01/`；每个异步job等待completed |
| 04版实际Player | `Validation/VRC-01ABC/20261103-probe-04/`，inputs.json、Player.log、environment-result、guards及退出码 |
| 04版独立消费者 | 同目录`relocated-outputs/{blender-consumer,unity-consumer}.json`，分别5/5；Unity7000.0.0a12与Blender5.2.2 LTS |
| 最终05版及冻结输入 | `Validation/VRC-01ABC/20261103-probe-05/{summary,inputs,environment-result,fbx-guards}.json`；39/39、15/15、退出0，33份产品源/资产输入运行后无漂移 |
| 最终05版独立消费者 | 同目录`relocated-outputs/{blender-consumer,unity-consumer}.json`，分别5/5；逐面拓扑/材质/UV/法线均对照独立预期 |
| 格式隔离与失败 | `Tools/VRC/Interop/Runs/`，保留empty model、metadata和额外geometric rotation差异 |

## 环境派生变化与边界

Unity首次构建将初始模板的URP资产和部分设置序列化为当前引擎/包结构，当前有9份原有文件派生差异，详见`Validation/VRC-00/20261103-122155/target-first-build-derived-deltas.json`及Git差异。包含URP14→15序列化字段、shader预筛选、Input/AppUI配置和预加载登记等；不代表更换URP包版本。manifest、lock及ProjectVersion未改；不得将派生修改混称用户原本修改。后续不再调用全局SaveAssets，只保存本工具拥有的资产。

构建报告出现510/512条警告，主体为现有Sentis Shader编译，完整消息/分组已留档；不是零警告构建。Editor曾观察到两条`On demand scheduler protocol error`断言，尚未归因为产品；实际Player日志及门槛另列，不能清空Console后宣称没有。Development Player退出仍有Unity原生MemoryLeaks诊断，需要保留并在07耐久/基线对照中处理；当前只证明跟踪的预览RT、mesh、material、texture销毁，不承诺全进程无泄漏。

完整OBJ/GLB、完整FBX继承/多UV/高级材质、用户库事务、完整编辑内核、正式UI、三范围导出/原子发布、旧SDD迁入、100轮资源/性能以及非Development候选尚未交付。额外GeometricRotation叠加非均匀比例的差异保留到03，未降低该批目标。人工IME和正式视觉验收属于04/07，未以自动事件代替。

## 接续

01A/01B/01C所有必要门槛已关闭，实际入口与冻结输入登记于批次总览。下一入口为02A的RoomDocument/LibraryDraft、独立历史和共享预算；房间使用全局placements组织关系，定义bounds约定需防止meshTransform重复应用。未执行SCM提交/签入或修改源项目。
