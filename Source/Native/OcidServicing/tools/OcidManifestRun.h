// 统一入口声明：ocid_manifest 的全部命令实现都在这一个函数里。
// ocid_manifest.exe / ocid_wssm.exe 各自提供 main 并调用它。
#pragma once

// argc/argv 与 main 的参数同形（UTF-8 窄字符）；返回 0 = 成功。
int OcidManifestRun(int argc, char** argv);
