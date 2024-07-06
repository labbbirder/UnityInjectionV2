using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BBBirder.UnityInjection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class PatchTargetTests : IInjectionProvider
{
    [Test]
    public void Replace_Class_InstanceMethod()
    {
        InjectionDriver.Instance.InstallAllAssemblies();
        Assert.AreEqual("Hijack end ", new ClassModel().GetString("end"));
    }

    [Test]
    public void Replace_Class_StaticMethod()
    {
        InjectionDriver.Instance.InstallAllAssemblies();
        Assert.AreEqual("Hijack hi, bbbirder", ClassModel.GetStringStatic("hi,", "bbbirder"));
    }

    [Test]
    public void Replace_Class_InstanceMethod_WithByRefParameters()
    {
        InjectionDriver.Instance.InstallAllAssemblies();
        var name = "Lau";
        Assert.AreEqual("Hijack hi, Leo", new ClassModel()
        {
            Name = "Leo"
        }.GetStringByRef(ref name));
        Assert.AreEqual("bbbirder", name);
    }

    [Test]
    public void Replace_Class_InstanceMethod_WithOutParameters()
    {
        InjectionDriver.Instance.InstallAllAssemblies();
        new ClassModel()
        {
            Name = "Lau"
        }.GetStringOut(out var name);
        Assert.AreEqual("bbbirder", name);
    }

    [Test]
    public void Replace_Struct_InstanceMethod()
    {
        InjectionDriver.Instance.InstallAllAssemblies();
        var inst = new ClassModel.NestedValueType();
        inst.SetName("Lau");
        Assert.AreEqual("bbbirder", inst.Name);
    }

    // // A UnityTest behaves like a coroutine in Play Mode. In Edit Mode you can use
    // // `yield return null;` to skip a frame.
    // [UnityTest]
    // public IEnumerator PatchTargetTestsWithEnumeratorPasses()
    // {
    //     // Use the Assert class to test conditions.
    //     // Use yield to skip a frame.
    //     yield return null;
    // }

    public IEnumerable<InjectionInfo> ProvideInjections()
    {
        // var fClassInst = new Func<string, string>(Model.Get)
        yield return new InjectionInfo<Func<ClassModel, string, string>>(typeof(ClassModel).GetMethod("GetString"), raw =>
        {
            return (inst, prefix) =>
            {
                return raw(inst, "Hijack " + prefix);
            };
        });
        yield return new InjectionInfo<Func<string, string, string>>(ClassModel.GetStringStatic, raw => (prefix, name) =>
        {
            return raw("Hijack " + prefix, name);
        });
        yield return new InjectionInfo<ClassModel.ByRefInvoker>(typeof(ClassModel).GetMethod("GetStringByRef"), raw => (ClassModel m, ref string name) =>
        {
            var ret = "Hijack " + raw(m, ref name);
            name = "bbbirder";
            return ret;
        });
        yield return new InjectionInfo<ClassModel.OutInvoker>(typeof(ClassModel).GetMethod("GetStringOut"), raw => (ClassModel m, out string name) =>
        {
            name = "bbbirder";
        });
        yield return new InjectionInfo<ClassModel.NestedValueType.Invoker>(typeof(ClassModel.NestedValueType).GetMethod("SetName"), raw => (ref ClassModel.NestedValueType m, string name) =>
        {
            raw(ref m, name);
            m.Name = "bbbirder";
        });
    }
}

class ClassModel
{
    public string Name { get; set; }
    public string GetString(string prefix)
    {
        return prefix + " " + Name;
    }

    public static string GetStringStatic(string prefix, string name)
    {
        return prefix + " " + name;
    }

    public delegate string ByRefInvoker(ClassModel m, ref string name);
    public string GetStringByRef(ref string name)
    {
        name = Name;
        return "hi, " + name;
    }

    public delegate void OutInvoker(ClassModel m, out string name);
    public void GetStringOut(out string name)
    {
        name = Name;
    }

    public class Nested
    {
        public string Name { get; set; }
        public string GetName(string prefix)
        {
            return prefix + " " + Name;
        }
    }

    public struct NestedValueType
    {
        public delegate void Invoker(ref NestedValueType m, string name);
        public string Name { get; set; }
        public void SetName(string name)
        {
            Name = name;
        }
    }
}
