﻿using System.Reflection;

namespace OpenSmsPlatform.Common
{
    public static class MethodInfoExtensions
    {
        public static string GetFullName(this MethodInfo method)
        {
            if (method.DeclaringType == null)
            {
                return $@"{method.Name}";
            }

            return $"{method.DeclaringType.FullName}.{method.Name}";
        }
    }
}
