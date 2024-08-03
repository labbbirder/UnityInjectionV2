using UnityEngine;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cysharp.Threading.Tasks;
using UnityEngine.Profiling;
using BBBirder.UnityInjection;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks.CompilerServices;
using System.Linq;
using System.Reflection;

[assembly: SuppressAutoInjection] // use this attribute to disable auto enable replacements
public class Demo : MonoBehaviour
{
    void TestRef<R>(ref R v)
    {
        var wr = __makeref(v);
        ref var val = ref __refvalue(wr, Vector3);
        val.Set(0.1f, 0.2f, 0.1231f);
        // val = new Vector3(0.1f, 0.2f, 0.1231f);
    }
    async void Start()
    {
        print("press SPACE to replace methods");
        print("press B to test basic method replacement");
        print("press A to test AOP implement");
        var action = UniTask.Create(async () =>
        {
            await UniTask.Delay(100);
            print("act1");
        });
        var action2 = UniTask.Action(async () =>
        {
            await UniTask.Delay(100);
            print("act2");
        });
        await UniTask.Delay(1000);
        action.ContinueWith(() => print("cont 1"));
        action2();

        var v = Vector3.forward;
        TestRef(ref v);
        print(v);
    }

    // struct MyMethodBuilder
    // {

    // }
    // struct MyAwaiter : INotifyCompletion
    // {
    //     public string GetResult() => "";
    //     public bool IsCompleted => true;
    //     public void OnCompleted(Action continuation)
    //     {
    //         throw new NotImplementedException();
    //     }
    // }
    // [AsyncMethodBuilder(typeof(MyMethodBuilder))]
    // struct MyTask
    // {
    //     public MyAwaiter GetAwaiter() => new MyAwaiter();
    // }
    // async MyTask tes()
    // {
    //     var res = await tes();
    // }
    async void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            InjectionDriver.Instance.InstallAllAssemblies();
        }
        if (Input.GetKeyDown(KeyCode.B))
        {
            Debug.Log("2 = " + DemoModal.ReturnTwoStatic());
            Debug.Log("2 = " + new DemoModal().ReturnTwoInstance());
            Debug.Log("4 + 1.2f = " + new DemoModal().Add(4, 1.2f));
        }
        if (Input.GetKeyDown(KeyCode.A))
        {
            await HeavyJobAsync("coding");
            // print("res: ");
        }
    }

    [AOP]
    public async UniTask HeavyJobAsync(string jobName)
    {
        for (int i = 0; i < 10; i++)
        {
            await UniTask.Delay(200);
            // Debug.Log($"do heavy job {jobName} {i}...");
        }
    }

    // [AOP]
    public async UniTask HeavyJobAsync1(string jobName)
    {
        // print(100);
        await UniTask.Delay(200);
        // print(200);
        await UniTask.Delay(200);
        // print(300);
        await UniTask.Delay(200);
        // print(400);
        await UniTask.Delay(200);
        // print(500);
        await UniTask.Delay(200);
        // print(600);
        // return 1231;
    }
    public async Task<int> HeavyJobAsync2(string jobName)
    {
        print(100);
        await UniTask.Delay(200);
        print(200);
        await UniTask.Delay(200);
        print(300);
        return 1231;
    }

    class AOPAttribute : DecoratorAttribute
    {
        // protected struct Stubs<R>
        // {
        //     public string name;
        //     public void OnEnter(InvocationInfo<R> invocation)
        //     {
        //         Debug.Log("start " + name);
        //     }
        //     public void OnExit()
        //     {
        //         Debug.Log("finish " + name);
        //     }
        // }
        public void OnExit()
        {
            Debug.Log("finish ");
        }

        protected override R Decorate<R>(InvocationInfo<R> invocation)
        {
            // var name = invocation.Arguments[1] as string;
            // var noGC = new Stubs<R>()
            // {
            //     name = name,
            // };
            // Debug.Log("start " + name);
            // Debug.Log(typeof(R));
            // Debug.Log(targetInfo);
            // Debug.Log(invocation.Method);
            // // noGC.OnEnter(invocation);
            // var attr = (targetInfo as MethodInfo).GetCustomAttributes(typeof(AsyncStateMachineAttribute), false)
            //     .FirstOrDefault() as AsyncStateMachineAttribute;
            // if (attr != null)
            // {
            //     var fsmType = attr.StateMachineType;
            //     var builderType = fsmType.GetField("<>t__builder").FieldType;
            //     print(fsmType);
            //     print(builderType);
            // }

            var r = invocation.FastInvoke();

            if (IsAsyncMethod)
            {
                Profiler.BeginSample("asymc invoke");
                if (typeof(R) == typeof(UniTask))
                {
                    var wr = __makeref(r);
                    ref var uni = ref __refvalue(wr, UniTask);
                    uni = uni.ContinueWith(() => { print("finoish"); });
                    // uni.ContinueWith(() => Debug.Log(123123));
                    // r = (R)(object)uni;

                    // Debug.Log("is unitask " + r);
                }
                // else if (typeof(R).GetGenericTypeDefinition() == typeof(UniTask<>))
                // {
                //     var ts = new UniTaskCompletionSource();
                //     invocation.GetAwaiter(r).OnCompleted(() =>
                //     {
                //         ts.TrySetResult();
                //         // Debug.Log("cont with ");
                //     });
                //     return (R)(object)ts.Task;
                // }
                else
                {
                    Profiler.BeginSample("invocation.GetAwaiter");
                    invocation.GetAwaiter(r).OnCompleted(OnExit);
                    // Debug.Log("not unitask " + r.GetType());
                    Profiler.EndSample();

                }
                Profiler.EndSample();
                // UniTask.Action(async () =>
                // {
                //     await r;
                // }).Invoke();
            }
            else
            {
                OnExit();
            }
            return r;
        }
    }

    internal class MethodReplacer : IInjectionProvider
    {
        public IEnumerable<InjectionInfo> ProvideInjections()
        {
            yield return new InjectionInfo<Func<int>>(DemoModal.ReturnTwoStatic, raw => () =>
            {
                return 2;
            });
            yield return new InjectionInfo<Func<DemoModal, int>>(
                ReflectionHelper.GetMethod(() => default(DemoModal).ReturnTwoInstance()),
                raw => (inst) =>
                {
                    return 2;
                }
            );
            yield return new InjectionInfo<Func<DemoModal, int, float, float>>(
                ReflectionHelper.GetMethod(() => default(DemoModal).Add(0, 0)),
                raw => (inst, i, f) =>
                {
                    return i + f;
                }
            );
            yield return new InjectionInfo<Action<object>>(
                Debug.Log,
                raw => o =>
                {
                    raw("[unity log] " + o);
                }
            );
        }
    }
}
