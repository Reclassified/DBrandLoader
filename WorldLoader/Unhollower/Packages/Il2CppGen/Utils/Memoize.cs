﻿using System;
using System.Collections.Generic;

namespace WorldLoader.Il2CppGen.Generator.Utils;

public class Memoize<TParam, TResult>
{
    private readonly Dictionary<int, TResult> _cache = new();
    private readonly Func<TParam, TResult> _func;

    public Memoize(Func<TParam, TResult> func) => _func = func;

    public TResult Get(TParam param)
    {
        if (_cache.TryGetValue(param.GetHashCode(), out var result))
        {
            return result;
        }

        result = _func(param);
        _cache.Add(param.GetHashCode(), result);
        return result;
    }
}
