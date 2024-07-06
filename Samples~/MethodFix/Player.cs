using System;
using System.Reflection;
using System.Text;
using com.bbbirder.UnityInjection;
using UnityEngine;

partial class Player : Battler
{
    StringBuilder sb = new();
    FieldInfo fi;
    unsafe void Awake()
    {
        (this as IDataProxy).OnSetProperty += (s) => print("on set " + s);
        (this as IDataProxy).OnGetProperty += (s) => print("on get " + s);

        this.enabled = true;

        fi = sb.GetType().GetField("m_ChunkChars", BindingFlags.NonPublic | BindingFlags.Instance);
        sb.Append("Good");
        const int CACHE_COUNT = 512;
        var s = "";
        var ls = "";
        for (int i = 0; i < CACHE_COUNT; i++)
        {
            ls += i.ToString();
            if (s.Contains(i.ToString())) continue;
            s += i.ToString();
        }
        print(s);
        print(s.Length);
        print(ls);
        print(ls.Length);
        fixed (char* w = s)
        {
            w[1] = '!';
        }
        print(s);
    }
    string[] ints;
    void Update()
    {
        var s = 1.ToString();
    }

}
