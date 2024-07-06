# UnityInjection2

Unity 注入模块，可以运行时改变被注入函数实现。

![mono support](http://img.shields.io/badge/Mono-support-green)
![il2cpp support](http://img.shields.io/badge/IL2CPP-support-green)
![GitHub last commit](http://img.shields.io/github/last-commit/labbbirder/UnityInjectionV2)
![GitHub package.json version](http://img.shields.io/github/package-json/v/labbbirder/UnityInjectionV2)

已验证版本:

|          |       Editor       |       IL2CPP       |        Mono        |
| :------: | :----------------: | :----------------: | :----------------: |
| 2021.3.x | :heavy_check_mark: | :heavy_check_mark: | :heavy_check_mark: |
| 2022.3.x | :heavy_check_mark: | :heavy_check_mark: | :heavy_check_mark: |
| 2023.3.x | :heavy_check_mark: | :heavy_check_mark: | :heavy_check_mark: |
| 6000.0.x | :heavy_check_mark: | :heavy_check_mark: | :heavy_check_mark: |
| Tuanjie  | :heavy_check_mark: | :heavy_check_mark: | :heavy_check_mark: |

> 包括： (Mac + Windows) x (IL2CPP + Mono + Editor) x $(U3D_{tj}+\sum_{v=2021.3}^{6000.0}U3D_v )$

## 这是Unity Injection V2

这是[UnityInjection](https://github.com/labbbirder/UnityInjection)的V2版本，经过一段漫长时间的自测，目前比较鲁棒。V2全面优于V1，主要体现在：

- 兼容性非常大的提升。目前主流系列Unity版本全部测试通过；Mac、Windows平台全部测试通过；IL2CPP、Mono全部测试通过
- 脚本的编译重载近乎没有额外负担，对比之下，V1需要额外重载一次domain
- 接口更加简洁了，傻瓜式调用
- 支持ref、in、out等关键字
- 支持struct方法修改
- 修复了第三方插件冲突问题
- 构建时的织入改为在UnityLinker之后进行，如此理论上合理一些
- 完备的测试用例
- 支持自定义织入逻辑

> 仍然有很多非常有趣的特性尚未实现。 ;)


## Quick Start

### Installation

#### install via git url

step 1. 安装依赖库：[DirectRetrieveAttribute](https://github.com/labbbirder/DirectRetrieveAttribute#安装)

step 2. 通过 git url 安装

#### install via openupm (recommend)

execute command line：

```bash
openupm add com.bbbirder.unity-injection
```

### Basic Usage

一个修改`Debug.Log`的例子

```csharp
using BBBirder.UnityInjection;

public class FirstPatch
{
    void Init()
    {
        // InjectionDriver.Instance.InstallAllAssemblies();
        Print("Any help?");
    }

    static void Print(object obj)
    {
        Debug.Log(obj);
    }

    // implement IInjectionProvider
    internal class MethodReplacer : IInjectionProvider
    {
        public IEnumerable<InjectionInfo> ProvideInjections()
        {
            yield return new InjectionInfo<Action<object>>(
                Print,         // replace it
                raw => obj =>  // with me
                {
                    raw("[my log] " + obj);
                }
            );
        }
    }
}

```

自定类继承自`IInjectionProvider`，并实现接口

`ProvideInjections`中返回一个或多个`InjectionInfo`


> 通过调用`InjectionDriver.Instance.InstallAllAssemblies()`使InjectionInfo失效，但通常不需要手动调用，默认在加载程序集时自动生效。可以在特定程序集标记`[assembly: SuppressAutoInjection]`来禁止这个默认行为。


## 更多用法

更多使用方法参考附带的 Sample 工程

## Possible Problems

|                           Problem                           | Reason                                          | Solution                                                                                      |
| :---------------------------------------------------------: | :---------------------------------------------- | :-------------------------------------------------------------------------------------------- |
|                  注入时未搜索到标记的方法                   | `Managed Stripping Level`过高，Attribute 被移除 | 降低 Stripping Level 或 [保留代码](https://docs.unity3d.com/Manual/ManagedCodeStripping.html) |
| 注入时报`UnauthorizedAccessException`或`cannot access file` | 文件访问权限不够                                | 管理员运行 或 修改目标文件夹的安全设置（属性-安全-编辑，添加当前用户的完全控制）              |

## How it works

UnityInjection 在编译时织入，不用担心运行时兼容性

织入时机：

- 编辑器时：Bee-Driven Compilation Finish 和 Domain Reload Start 间空档期
- 运行时：实现了两种方式，以备未来可能的新Unity版本兼容问题
  - UnityLinker Stripping Finish 和 Specific Platform Building Start 间空档期
  - 手动收集程序集，提前织入


<!-- Todo List

- replace source generate 5h
- reload time compare 1h
- delegate type shared 2h
- low level cecil methods 3h -->
