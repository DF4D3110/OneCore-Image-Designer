// OCID - 程序集级属性（必须放在**会被编译**的文件里）
//
// ⚠ 别把这个属性挪回 `AssemblyInfo.cs`：那个文件在 csproj 里被
//   `<Compile Remove="AssemblyInfo.cs" />` 排除掉了（WPF 会在 obj 下自己生成一份
//    theme info），所以写在里面的程序集属性**完全不生效**，而且没有任何提示 ——
//   表现只是测试里报 `CS0122: "X" 不可访问，因为它具有一定的保护级别`。
//
// 同理不能用 csproj 的 `<InternalsVisibleTo>` 项：本项目设了
// `<GenerateAssemblyInfo>false</GenerateAssemblyInfo>`，SDK 不会把它变成属性。

using System.Runtime.CompilerServices;

// 离线测试要直接验服务栈内部：真正编码注册表字节、决定"该不该写"的代码按设计是
// internal（外部只该经 OcidSmiService 的公开入口）。没有这个属性，测试只能看到公开面，
// 而那恰好是**不含跨语言边界**的那一层。
[assembly: InternalsVisibleTo("OneCore.Image.Designer.OfflineTests")]
