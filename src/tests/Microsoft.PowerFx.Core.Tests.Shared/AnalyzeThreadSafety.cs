﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Threading;
using Microsoft.PowerFx.Core.Functions;
using Xunit;

namespace Microsoft.PowerFx.Core.Tests
{
    /// <summary>
    /// Analyze assemblies for thread safety issues. 
    /// </summary>
    public class AnalyzeThreadSafety
    {
        public static bool IsThreadSafeImmutable(Type t)
        {
            int errors = 0;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            // Check out all fields and properties. 
            foreach (var prop in t.GetProperties(flags)) 
            {
                var name = prop.Name;
                if (prop.CanWrite)
                {
                    var isInitKeyword = HasInitKeyword(prop);
                    if (!isInitKeyword)
                    {
                        // No mutable properties allowed. Init only ok. 
                        Debugger.Log(0, string.Empty, $"{t.Name}.{name} has setter\r\n");
                        errors++;
                    }
                }

                Assert.True(prop.CanRead);

                var propType = prop.PropertyType;

                if (!IsTypeImmutable(propType))
                {
                    // valuetypes are copies, so no contention
                    if (!prop.PropertyType.IsValueType) 
                    {
                        // Fail. 
                        Debugger.Log(0, string.Empty, $"{t.Name}.{name} returns mutable value\r\n");
                        errors++;
                    }
                }
            }
            
            foreach (var field in t.GetFields(flags))
            {
                var name = field.Name;

                if (name.StartsWith("<"))
                {
                    // Ignore compile generated fields.
                    continue;
                }

                // ReadOnly
                if (!field.IsInitOnly)
                {
                    Debugger.Log(0, string.Empty, $"{t.Name}.{name} is not readonly\r\n");
                    errors++;
                }

                if (field.GetCustomAttributes<ThreadSafeProtectedByLockAttribute>().Any() ||
                    IsTypeConcurrent(field.FieldType))
                {
                    continue;
                }

                if (!IsTypeImmutable(field.FieldType))
                {
                    // Fail. 
                    Debugger.Log(0, string.Empty, $"{t.Name}.{name} returns mutable value\r\n");
                    errors++;
                }
            }

            if (errors > 0)
            {
                Debugger.Log(0, string.Empty, $"\r\n");
                return false;
            }

            return true;
        }
                
        // Verify there are no "unsafe" static fields that could be threading issues.
        // Bugs - list of field types types that don't work. This should be driven to 0. 
        // BugNames - list of "Type.Field" that don't work. This should be driven to 0. 
        public static void CheckStatics(Assembly asm, HashSet<Type> bugsFieldType, HashSet<string> bugNames)
        {
            // Being immutable is the easiest way to be thread safe. Tips:
            // `const` fields are safest - but that can only apply to literals. 
            //
            // readonly fields are safe if the field type is also immutable. So either:
            // - change to immutable interface, like `readonly Dict<string,int> _keys` --> `readonly IReadOnlyDict<string,int> _keys`
            // - mark type as immutable via [ImmutableObject] attribute. 
            //
            // Compiler properties will generate a backing field - we still catch the field via reflection.
            // `int Prop {get;set; } = 123`  // mutable backing field! 
            // `int Prop {get; } = 123`      // readonly backing field! 
            var errors = new List<string>();

            var total = 0;
            foreach (var type in asm.GetTypes())
            {
                // Reflecting over fields will also find compiler-generated "backing fields" from static properties. 
                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var name = $"{type.Name}.{field.Name}";
                    total++;

                    if (field.Attributes.HasFlag(FieldAttributes.Literal))
                    {
                        continue;  // 'const' keyword, 
                    }

                    if (type.Name.Contains("<>") ||
                        type.Name.Contains("<PrivateImplementationDetails>"))
                    {
                        continue; // exclude compiler generated closures. 
                    }

                    // Field has is protected by a lock.
                    if (field.GetCustomAttributes<ThreadSafeProtectedByLockAttribute>().Any())
                    {
                        continue;
                    }

                    if (bugsFieldType != null && bugsFieldType.Contains(field.FieldType))
                    {
                        continue;
                    }

                    if (bugNames != null && bugNames.Contains(name))
                    {
                        continue;
                    }

                    if (field.GetCustomAttribute<ThreadStaticAttribute>() != null)
                    {
                        // If field is marked [ThreadStatic], then each thread gets its own copy.
                        // It also implies the author thought about threading. 
                        continue;
                    }

                    if (IsFieldVolatile(field))
                    {
                        // If a field was marked volatile, then assume the author thought through the threading.
                        continue;
                    }

                    // Is it readonly? Const?
                    if (!field.Attributes.HasFlag(FieldAttributes.InitOnly))
                    {
                        // Mutable static field! That's bad.  
                        errors.Add($"{name} is not readonly");
                        continue;
                    }

                    // Is it a
                    if (!IsTypeImmutable(field.FieldType))
                    {
                        errors.Add($"{name} readonly, but still a mutable type {field.FieldType}");
                    }

                    // Safe! The static field is readonly and set to an immutable object. 
                }
            }

            // Sanity check that we actually ran the test. 
            Assert.True(total > 10, "failed to find fields");

            // Batch up errors so we can see all at once. 
            Assert.Empty(errors);
        }

        // Does the property have 'init' keyword.
        private static bool HasInitKeyword(PropertyInfo prop)
        {
            var attrs = prop.SetMethod.ReturnParameter.GetRequiredCustomModifiers();

            // Can't use typeof() because it may not be available in .Net 5.0+
            // So check type name instead. 
            // typeof(System.Runtime.CompilerServices.IsExternalInit)
            bool hasInit = attrs.Any(type => type.Name == "IsExternalInit");

            return hasInit;
        }

        // Does this type, or any of its base types or interfaces have [ThreadSafeImmutable].
        public static bool InheritsThreadSafeImmutable(Type type)
        {
            var interfaces = type.GetInterfaces();
            foreach (var x in interfaces)
            {
                if (x.GetCustomAttributes().OfType<ThreadSafeImmutableAttribute>().Any())
                {
                    return true;
                }
            }
            
            var attribute = type.GetCustomAttribute<ThreadSafeImmutableAttribute>(inherit: false);

            if (attribute != null)
            {
                return true;
            }

            return false;
        }

        // Check all types in the assembly list that have or inherit [ThreadSafeImmutable].
        // See output window for verbose details on failures.
        // Excuse knownFailures (thhose should be tracked down and fixed separately). 
        public static void CheckImmutableTypes(Assembly[] assemblies, HashSet<Type> knownFailures = null)
        {
            var countPassed = new HashSet<string>();
            var countFailed = new HashSet<string>();

            foreach (var assembly in assemblies)
            {
                foreach (Type type in assembly.GetTypes())
                {
                    if (knownFailures != null && knownFailures.Contains(type))
                    {
                        continue;
                    }

                    if (type.Name.StartsWith("<", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // exclude compiler generated closures. 
                    }

                    // includes base types 
                    if (!InheritsThreadSafeImmutable(type))
                    {
                        continue;
                    }

                    // Common pattern is a writeable derived type (like Dict vs. IReadOnlyDict). 
                    var attrNotSafe = type.GetCustomAttribute<NotThreadSafeAttribute>(inherit: false);
                    if (attrNotSafe != null)
                    {
                        var attribute = type.GetCustomAttribute<ThreadSafeImmutableAttribute>(inherit: false);
                        if (attribute != null)
                        {
                            Assert.True(false); // Class can't have both safe & unsafe together. 
                        }

                        continue;
                    }

                    bool ok = AnalyzeThreadSafety.IsThreadSafeImmutable(type);
                    if (ok)
                    {
                        countPassed.Add(type.FullName);
                    }
                    else
                    {
                        countFailed.Add(type.FullName);
                    }
                }
            }

            Debugger.Log(0, string.Empty, $"{countPassed.Count} passed, {countFailed.Count} failed. {countPassed.Count + countFailed.Count} total.\r\n");

            string failMsg = string.Join(", ", countFailed);
            Assert.True(countFailed.Count == 0, $"Types failed. See output window: {failMsg}");
        }

        private static bool IsTypeConcurrent(Type type)
        {
            if (type.IsGenericType)
            {
                var genericDef = type.GetGenericTypeDefinition();
                if (genericDef == typeof(ConcurrentDictionary<,>))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsFieldVolatile(FieldInfo field)
        {
            var isVolatile = field
                .GetRequiredCustomModifiers()
                .Any(x => x == typeof(IsVolatile));
            return isVolatile;
        }

        // For other custom types, mark with [ThreadSafeImmutable] attribute.
        private static readonly HashSet<Type> _knownImmutableTypes = new HashSet<Type>
        {
            // Primitives
            typeof(object),
            typeof(string),
            typeof(System.Type),
            typeof(Random),
            typeof(DateTime),
            typeof(System.Text.RegularExpressions.Regex),
            typeof(System.Numerics.BigInteger),
            typeof(NumberFormatInfo),
            typeof(CultureInfo),
            typeof(TimeZoneInfo),

            // Generics        
            typeof(IReadOnlyDictionary<,>),
            typeof(IReadOnlyCollection<>),
            typeof(IReadOnlyList<>),
            typeof(Nullable<>),
            typeof(IEnumerable<>),
            typeof(KeyValuePair<,>),
            typeof(ISet<>),
            typeof(IServiceProvider),

            // Concurrent types are thread safe.
            typeof(ReaderWriterLockSlim),
            typeof(System.Resources.ResourceManager)
        };

        // If the instance is readonly, is the type itself immutable ?
        internal static bool IsTypeImmutable(Type t)
        {
            if (t.IsArray)
            {
                // Arrays are definitely not safe - their elements can be mutated.
                return false;
            }

            if (t.IsPrimitive || t.IsEnum || t == typeof(decimal))
            {
                return true;
            }

            var attr = t.GetCustomAttribute<ThreadSafeImmutableAttribute>();
            if (attr != null)
            {
                return true;
            }

            // Collection classes should be a IReadOnly<T>. Verify their T is also safe.
            if (t.IsGenericType)
            {
                var genericDef = t.GetGenericTypeDefinition();
                if (_knownImmutableTypes.Contains(genericDef))
                { 
                    var typeArgs = t.GetGenericArguments();
                    foreach (var arg in typeArgs)
                    {
                        var isArgSafe = IsTypeImmutable(arg) || arg.IsValueType;
                        if (!isArgSafe)
                        {
                            return false;
                        }
                    }

                    return true;
                }
            }

            if (_knownImmutableTypes.Contains(t))
            {
                return true;
            }

            // Treat delegates as immutable. They're just static functions. 
            // If the delegate is closed over mutable state, those arugments would show up as fields and be caught.
            if (t.BaseType == typeof(MulticastDelegate))
            {
                return true;
            }

            return false;
        }
    }
}
