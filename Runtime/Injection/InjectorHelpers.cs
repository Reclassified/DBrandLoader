﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using WorldLoader.Il2CppGen.Internal;
using WorldLoader.Il2CppGen.Internal.XrefScans;
using Il2CppGen.Runtime.Runtime;
using Il2CppGen.Runtime.Runtime.VersionSpecific.Assembly;
using Il2CppGen.Runtime.Runtime.VersionSpecific.Class;
using Il2CppGen.Runtime.Runtime.VersionSpecific.FieldInfo;
using Il2CppGen.Runtime.Runtime.VersionSpecific.Image;
using Il2CppGen.Runtime.Runtime.VersionSpecific.MethodInfo;
using WorldLoader.HookUtils;
using WorldLoader.Il2CppGen.Internal.Extensions;

namespace Il2CppGen.Runtime.Injection
{
    internal static unsafe class InjectorHelpers
    {
        internal static Assembly Il2CppMscorlib = typeof(Il2CppSystem.Type).Assembly;
        internal static INativeAssemblyStruct InjectedAssembly;
        internal static INativeImageStruct InjectedImage;
        internal static ProcessModule Il2CppModule = Process.GetCurrentProcess()
            .Modules.OfType<ProcessModule>()
            .Single((x) => x.ModuleName is "GameAssembly.dll" or "GameAssembly.so" or "UserAssembly.dll");

        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern IntPtr LoadLibrary([MarshalAs(UnmanagedType.LPStr)] string lpFileName);


        internal static IntPtr Il2CppHandle = LoadLibrary("GameAssembly.dll");

        internal static readonly Dictionary<Type, OpCode> StIndOpcodes = new()
        {
            [typeof(byte)] = OpCodes.Stind_I1,
            [typeof(sbyte)] = OpCodes.Stind_I1,
            [typeof(bool)] = OpCodes.Stind_I1,
            [typeof(short)] = OpCodes.Stind_I2,
            [typeof(ushort)] = OpCodes.Stind_I2,
            [typeof(int)] = OpCodes.Stind_I4,
            [typeof(uint)] = OpCodes.Stind_I4,
            [typeof(long)] = OpCodes.Stind_I8,
            [typeof(ulong)] = OpCodes.Stind_I8,
            [typeof(float)] = OpCodes.Stind_R4,
            [typeof(double)] = OpCodes.Stind_R8
        };

        private static void CreateInjectedAssembly()
        {
            InjectedAssembly = UnityVersionHandler.NewAssembly();
            InjectedImage = UnityVersionHandler.NewImage();

            InjectedAssembly.Name.Name = Marshal.StringToHGlobalAnsi("InjectedMonoTypes");

            InjectedImage.Assembly = InjectedAssembly.AssemblyPointer;
            InjectedImage.Dynamic = 1;
            InjectedImage.Name = InjectedAssembly.Name.Name;
            if (InjectedImage.HasNameNoExt)
                InjectedImage.NameNoExt = InjectedAssembly.Name.Name;
        }

        internal static void Setup()
        {
            if (InjectedAssembly == null) CreateInjectedAssembly();
            GenericMethodGetMethod ??= FindGenericMethodGetMethod();
            GetTypeInfoFromTypeDefinitionIndex ??= FindGetTypeInfoFromTypeDefinitionIndex();
            ClassGetFieldDefaultValue ??= FindClassGetFieldDefaultValue();
            ClassInit ??= FindClassInit();
            ClassFromIl2CppType ??= FindClassFromIl2CppType();
            ClassFromName ??= FindClassFromName();
        }

        internal static long CreateClassToken(IntPtr classPointer)
        {
            long newToken = Interlocked.Decrement(ref s_LastInjectedToken);
            s_InjectedClasses[newToken] = classPointer;
            return newToken;
        }

        internal static void AddTypeToLookup<T>(IntPtr typePointer) where T : class => AddTypeToLookup(typeof(T), typePointer);
        internal static void AddTypeToLookup(Type type, IntPtr typePointer)
        {
            string klass = type.Name;
            if (klass == null) return;
            string namespaze = type.Namespace ?? string.Empty;
            var attribute = Attribute.GetCustomAttribute(type, typeof(Il2CppGen.Runtime.Attributes.ClassInjectionAssemblyTargetAttribute)) as Il2CppGen.Runtime.Attributes.ClassInjectionAssemblyTargetAttribute;

            foreach (IntPtr image in (attribute is null) ? IL2CPP.GetIl2CppImages() : attribute.GetImagePointers())
            {
                s_ClassNameLookup.Add((namespaze, klass, image), typePointer);
            }
        }

        internal static IntPtr GetIl2CppExport(string name)
        {
            if (!TryGetIl2CppExport(name, out var address))
            {
                throw new NotSupportedException($"Couldn't find {name} in {Il2CppModule.ModuleName}'s exports");
            }

            return address;
        }

        internal static bool TryGetIl2CppExport(string name, out IntPtr address)
        {
            address = GetProcAddress(Il2CppHandle, name);
            return address != IntPtr.Zero;
        }

        internal static IntPtr GetIl2CppMethodPointer(MethodBase proxyMethod)
        {
            if (proxyMethod == null) return IntPtr.Zero;

            FieldInfo methodInfoPointerField = Il2CppGenUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(proxyMethod);
            if (methodInfoPointerField == null)
                throw new ArgumentException($"Couldn't find the generated method info pointer for {proxyMethod.Name}");

            // Il2CppClassPointerStore calls the static constructor for the type
            Il2CppClassPointerStore.GetNativeClassPointer(proxyMethod.DeclaringType);

            IntPtr methodInfoPointer = (IntPtr)methodInfoPointerField.GetValue(null);
            if (methodInfoPointer == IntPtr.Zero)
                throw new ArgumentException($"Generated method info pointer for {proxyMethod.Name} doesn't point to any il2cpp method info");
            INativeMethodInfoStruct methodInfo = UnityVersionHandler.Wrap((Il2CppMethodInfo*)methodInfoPointer);
            return methodInfo.MethodPointer;
        }

        private static long s_LastInjectedToken = -2;
        private static readonly ConcurrentDictionary<long, IntPtr> s_InjectedClasses = new();
        /// <summary> (namespace, class, image) : class </summary>
        private static readonly Dictionary<(string _namespace, string _class, IntPtr imagePtr), IntPtr> s_ClassNameLookup = new();
        #region GenericMethod::GetMethod
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate Il2CppMethodInfo* d_GenericMethodGetMethod(Il2CppGenericMethod* gmethod, bool copyMethodPtr);
        //private static readonly d_GenericMethodGetMethod GenericMethodGetMethodDetour = new(ClassInjector.hkGenericMethodGetMethod);
        internal static d_GenericMethodGetMethod GenericMethodGetMethod;
        //internal static d_GenericMethodGetMethod GenericMethodGetMethodOriginal;
        private static d_GenericMethodGetMethod FindGenericMethodGetMethod()
        {
            var getVirtualMethodAPI = GetIl2CppExport(nameof(IL2CPP.il2cpp_object_get_virtual_method));
            Logger.Instance.LogTrace($"il2cpp_object_get_virtual_method: 0x{getVirtualMethodAPI.ToInt64().ToString("X2")}");

            var getVirtualMethod = XrefScannerLowLevel.JumpTargets(getVirtualMethodAPI).Single();
            Logger.Instance.LogTrace($"Object::GetVirtualMethod: 0x{getVirtualMethod.ToInt64().ToString("X2")}");

            var genericMethodGetMethod = XrefScannerLowLevel.JumpTargets(getVirtualMethod).Last();
            Logger.Instance.LogTrace($"GenericMethod::GetMethod: 0x{genericMethodGetMethod.ToInt64().ToString("X2")}");

            var targetTargets = XrefScannerLowLevel.JumpTargets(genericMethodGetMethod).Take(2).ToList();
            if (targetTargets.Count == 1) // U2021.2.0+, there's additional shim that takes 3 parameters
                genericMethodGetMethod = targetTargets[0];
            //GenericMethodGetMethodOriginal = NativeUtils.Detour(genericMethodGetMethod, GenericMethodGetMethodDetour);
            return Marshal.GetDelegateForFunctionPointer<d_GenericMethodGetMethod>(genericMethodGetMethod);
        }
        #endregion
        #region Class::FromName
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate Il2CppClass* d_ClassFromName(Il2CppImage* image, IntPtr _namespace, IntPtr name);
        private static Il2CppClass* hkClassFromName(Il2CppImage* image, IntPtr _namespace, IntPtr name)
        {
            while (ClassFromNameOriginal == null) Thread.Sleep(1);
            Il2CppClass* classPtr = ClassFromNameOriginal(image, _namespace, name);

            if (classPtr == null)
            {
                string namespaze = Marshal.PtrToStringAnsi(_namespace);
                string className = Marshal.PtrToStringAnsi(name);
                s_ClassNameLookup.TryGetValue((namespaze, className, (IntPtr)image), out IntPtr injectedClass);
                classPtr = (Il2CppClass*)injectedClass;
            }

            return classPtr;
        }
        private static readonly d_ClassFromName ClassFromNameDetour = new(hkClassFromName);
        internal static d_ClassFromName ClassFromName;
        internal static d_ClassFromName ClassFromNameOriginal;
        private static d_ClassFromName FindClassFromName()
        {
            var classFromNameAPI = GetIl2CppExport(nameof(IL2CPP.il2cpp_class_from_name));
            Logger.Instance.LogTrace($"il2cpp_class_from_name: 0x{classFromNameAPI.ToInt64().ToString("X2")}");

            var classFromName = XrefScannerLowLevel.JumpTargets(classFromNameAPI).Single();
            Logger.Instance.LogTrace($"Class::FromName: 0x{classFromName.ToInt64().ToString("X2")}");

            ClassFromNameOriginal = NativeUtils.Detour(classFromName, ClassFromNameDetour);
            return Marshal.GetDelegateForFunctionPointer<d_ClassFromName>(classFromName);
        }
        #endregion

        #region MetadataCache::GetTypeInfoFromTypeDefinitionIndex
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate Il2CppClass* d_GetTypeInfoFromTypeDefinitionIndex(int index);
        private static Il2CppClass* hkGetTypeInfoFromTypeDefinitionIndex(int index)
        {
            if (s_InjectedClasses.TryGetValue(index, out IntPtr classPtr))
                return (Il2CppClass*)classPtr;

            while (GetTypeInfoFromTypeDefinitionIndexOriginal == null) Thread.Sleep(1);
            return GetTypeInfoFromTypeDefinitionIndexOriginal(index);
        }
        private static readonly d_GetTypeInfoFromTypeDefinitionIndex GetTypeInfoFromTypeDefinitionIndexDetour = new(hkGetTypeInfoFromTypeDefinitionIndex);
        internal static d_GetTypeInfoFromTypeDefinitionIndex GetTypeInfoFromTypeDefinitionIndex;
        internal static d_GetTypeInfoFromTypeDefinitionIndex GetTypeInfoFromTypeDefinitionIndexOriginal;
        private static d_GetTypeInfoFromTypeDefinitionIndex FindGetTypeInfoFromTypeDefinitionIndex(bool forceICallMethod = false)
        {
            IntPtr getTypeInfoFromTypeDefinitionIndex = IntPtr.Zero;

            // il2cpp_image_get_class is added in 2018.3.0f1
            if (GetReady.Instance.UnityVersion < new Version(2018, 3, 0) || forceICallMethod)
            {
                // (Kasuromi): RuntimeHelpers.InitializeArray calls an il2cpp icall, proxy function does some magic before it invokes it
                // https://github.com/Unity-Technologies/mono/blob/unity-2018.2/mcs/class/corlib/System.Runtime.CompilerServices/RuntimeHelpers.cs#L53-L54
                IntPtr runtimeHelpersInitializeArray = GetIl2CppMethodPointer(
                    typeof(Il2CppSystem.Runtime.CompilerServices.RuntimeHelpers)
                        .GetMethod("InitializeArray", new Type[] { typeof(Il2CppSystem.Array), typeof(IntPtr) })
                );
                Logger.Instance.LogTrace($"Il2CppSystem.Runtime.CompilerServices.RuntimeHelpers::InitializeArray: 0x{runtimeHelpersInitializeArray.ToInt64().ToString("X2")}");

                var runtimeHelpersInitializeArrayICall = XrefScannerLowLevel.JumpTargets(runtimeHelpersInitializeArray).Last();
                if (XrefScannerLowLevel.JumpTargets(runtimeHelpersInitializeArrayICall).Count() == 1)
                {
                    // is a thunk function
                    Logger.Instance.LogTrace($"RuntimeHelpers::thunk_InitializeArray: 0x{runtimeHelpersInitializeArrayICall.ToInt64().ToString("X2")}");
                    runtimeHelpersInitializeArrayICall = XrefScannerLowLevel.JumpTargets(runtimeHelpersInitializeArrayICall).Single();
                }

                Logger.Instance.LogTrace($"RuntimeHelpers::InitializeArray: 0x{runtimeHelpersInitializeArrayICall.ToInt64().ToString("X2")}");

                var typeGetUnderlyingType = XrefScannerLowLevel.JumpTargets(runtimeHelpersInitializeArrayICall).ElementAt(1);
                Logger.Instance.LogTrace($"Type::GetUnderlyingType: 0x{typeGetUnderlyingType.ToInt64().ToString("X2")}");

                getTypeInfoFromTypeDefinitionIndex = XrefScannerLowLevel.JumpTargets(typeGetUnderlyingType).First();
            }
            else
            {
                var imageGetClassAPI = GetIl2CppExport(nameof(IL2CPP.il2cpp_image_get_class));
                Logger.Instance.LogTrace($"il2cpp_image_get_class: 0x{imageGetClassAPI.ToInt64().ToString("X2")}");

                var imageGetType = XrefScannerLowLevel.JumpTargets(imageGetClassAPI).Single();
                Logger.Instance.LogTrace($"Image::GetType: 0x{imageGetType.ToInt64().ToString("X2")}");

                var imageGetTypeXrefs = XrefScannerLowLevel.JumpTargets(imageGetType).ToArray();

                if (imageGetTypeXrefs.Length == 0)
                {
                    // (Kasuromi): Image::GetType appears to be inlined in il2cpp_image_get_class on some occasions,
                    // if the unconditional xrefs are 0 then we are in the correct method (seen on unity 2019.3.15)
                    getTypeInfoFromTypeDefinitionIndex = imageGetType;
                }
                else getTypeInfoFromTypeDefinitionIndex = imageGetTypeXrefs[0];
                if ((getTypeInfoFromTypeDefinitionIndex.ToInt64() & 0xF) != 0)
                {
                    Logger.Instance.LogTrace($"Image::GetType xref wasn't aligned, attempting to resolve from icall");
                    return FindGetTypeInfoFromTypeDefinitionIndex(true);
                }
                if (imageGetTypeXrefs.Count() > 1 && UnityVersionHandler.IsMetadataV29OrHigher)
                {
                    // (Kasuromi): metadata v29 introduces handles and adds extra calls, a check for unity versions might be necessary in the future

                    // Second call after obtaining handle, if there are any more calls in the future - correctly index into it if issues occur
                    var getTypeInfoFromHandle = XrefScannerLowLevel.JumpTargets(imageGetType).Last();
                    // Two calls, second one (GetIndexForTypeDefinitionInternal) is inlined
                    getTypeInfoFromTypeDefinitionIndex = XrefScannerLowLevel.JumpTargets(getTypeInfoFromHandle).Single();

                    // Xref scanner is sometimes confused about getTypeInfoFromHandle so we walk all the thunks until we hit the big method we need
                    while (XrefScannerLowLevel.JumpTargets(getTypeInfoFromTypeDefinitionIndex).Count() == 1)
                    {
                        getTypeInfoFromTypeDefinitionIndex = XrefScannerLowLevel.JumpTargets(getTypeInfoFromTypeDefinitionIndex).Single();
                    }
                }
            }

            Logger.Instance.LogTrace($"MetadataCache::GetTypeInfoFromTypeDefinitionIndex: 0x{getTypeInfoFromTypeDefinitionIndex.ToInt64().ToString("X2")}");

            GetTypeInfoFromTypeDefinitionIndexOriginal = NativeUtils.Detour(
                getTypeInfoFromTypeDefinitionIndex,
                GetTypeInfoFromTypeDefinitionIndexDetour
            );
            return Marshal.GetDelegateForFunctionPointer<d_GetTypeInfoFromTypeDefinitionIndex>(getTypeInfoFromTypeDefinitionIndex);
        }
        #endregion

        #region Class::FromIl2CppType
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate Il2CppClass* d_ClassFromIl2CppType(Il2CppType* type, bool throwOnError);

        /// Common version of the Il2CppType, the only thing that changed between unity version are the bitfields values that we don't use
        internal readonly struct Il2CppType
        {
            public readonly void* data;
            public readonly ushort attrs;
            public readonly Il2CppTypeEnum type;
            private readonly byte _bitfield;
        }

        private static Il2CppClass* hkClassFromIl2CppType(Il2CppType* type, bool throwOnError)
        {
            if ((nint)type->data < 0 && (type->type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS || type->type == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE))
            {
                s_InjectedClasses.TryGetValue((nint)type->data, out var classPointer);
                return (Il2CppClass*)classPointer;
            }

            return ClassFromIl2CppTypeOriginal(type, throwOnError);
        }
        private static d_ClassFromIl2CppType ClassFromIl2CppTypeDetour = new(hkClassFromIl2CppType);
        internal static d_ClassFromIl2CppType ClassFromIl2CppType;
        internal static d_ClassFromIl2CppType ClassFromIl2CppTypeOriginal;
        private static d_ClassFromIl2CppType FindClassFromIl2CppType()
        {
            var classFromTypeAPI = GetIl2CppExport(nameof(IL2CPP.il2cpp_class_from_il2cpp_type));
            Logger.Instance.LogTrace($"il2cpp_class_from_il2cpp_type: 0x{classFromTypeAPI.ToInt64().ToString("X2")}");

            var classFromType = XrefScannerLowLevel.JumpTargets(classFromTypeAPI).Single();
            Logger.Instance.LogTrace($"Class::FromIl2CppType: 0x{classFromType.ToInt64().ToString("X2")}");

            ClassFromIl2CppTypeOriginal = NativeUtils.Detour(classFromType, ClassFromIl2CppTypeDetour);
            return Marshal.GetDelegateForFunctionPointer<d_ClassFromIl2CppType>(classFromType);
        }
        #endregion

        #region Class::GetFieldDefaultValue
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate byte* d_ClassGetFieldDefaultValue(Il2CppFieldInfo* field, out Il2CppTypeStruct* type);
        private static byte* hkClassGetFieldDefaultValue(Il2CppFieldInfo* field, out Il2CppTypeStruct* type)
        {
            if (EnumInjector.GetDefaultValueOverride(field, out IntPtr newDefaultPtr))
            {
                INativeFieldInfoStruct wrappedField = UnityVersionHandler.Wrap(field);
                INativeClassStruct wrappedParent = UnityVersionHandler.Wrap(wrappedField.Parent);
                INativeClassStruct wrappedElementClass = UnityVersionHandler.Wrap(wrappedParent.ElementClass);
                type = wrappedElementClass.ByValArg.TypePointer;
                return (byte*)newDefaultPtr;
            }
            while (ClassGetFieldDefaultValueOriginal == null) Thread.Sleep(1);
            return ClassGetFieldDefaultValueOriginal(field, out type);
        }
        private static d_ClassGetFieldDefaultValue ClassGetFieldDefaultValueDetour = new(hkClassGetFieldDefaultValue);
        internal static d_ClassGetFieldDefaultValue ClassGetFieldDefaultValue;
        internal static d_ClassGetFieldDefaultValue ClassGetFieldDefaultValueOriginal;
        private static d_ClassGetFieldDefaultValue FindClassGetFieldDefaultValue(bool forceICallMethod = false)
        {
            // NOTE: In some cases this pointer will be MetadataCache::GetFieldDefaultValueForField due to Field::GetDefaultFieldValue being
            // inlined but we'll treat it the same even though it doesn't receive the type parameter the RDX register
            // doesn't get cleared so we still get the same parameters
            var classGetDefaultFieldValue = IntPtr.Zero;

            if (forceICallMethod)
            {
                // MonoField isn't present on 2021.2.0+
                var monoFieldType = Il2CppMscorlib.GetTypesSafe().SingleOrDefault((x) => x.Name is "MonoField");
                if (monoFieldType == null)
                    throw new Exception($"Unity {GetReady.Instance.UnityVersion} is not supported at the moment: MonoField isn't present in Il2Cppmscorlib.dll for unity version, unable to fetch icall");

                var monoFieldGetValueInternalThunk = GetIl2CppMethodPointer(monoFieldType.GetMethod(nameof(Il2CppSystem.Reflection.MonoField.GetValueInternal)));
                Logger.Instance.LogTrace($"Il2CppSystem.Reflection.MonoField::thunk_GetValueInternal: 0x{monoFieldGetValueInternalThunk.ToInt64().ToString("X2")}");

                var monoFieldGetValueInternal = XrefScannerLowLevel.JumpTargets(monoFieldGetValueInternalThunk).Single();
                Logger.Instance.LogTrace($"Il2CppSystem.Reflection.MonoField::GetValueInternal: 0x{monoFieldGetValueInternal.ToInt64().ToString("X2")}");

                // Field::GetValueObject could be inlined with Field::GetValueObjectForThread
                var fieldGetValueObject = XrefScannerLowLevel.JumpTargets(monoFieldGetValueInternal).Single();
                Logger.Instance.LogTrace($"Field::GetValueObject: 0x{fieldGetValueObject.ToInt64().ToString("X2")}");

                var fieldGetValueObjectForThread = XrefScannerLowLevel.JumpTargets(fieldGetValueObject).Last();
                Logger.Instance.LogTrace($"Field::GetValueObjectForThread: 0x{fieldGetValueObjectForThread.ToInt64().ToString("X2")}");

                classGetDefaultFieldValue = XrefScannerLowLevel.JumpTargets(fieldGetValueObjectForThread).ElementAt(2);
            }
            else
            {
                var getStaticFieldValueAPI = GetIl2CppExport(nameof(IL2CPP.il2cpp_field_static_get_value));
                Logger.Instance.LogTrace($"il2cpp_field_static_get_value: 0x{getStaticFieldValueAPI.ToInt64().ToString("X2")}");

                var getStaticFieldValue = XrefScannerLowLevel.JumpTargets(getStaticFieldValueAPI).Single();
                Logger.Instance.LogTrace($"Field::StaticGetValue: 0x{getStaticFieldValue.ToInt64().ToString("X2")}");

                var getStaticFieldValueInternal = XrefScannerLowLevel.JumpTargets(getStaticFieldValue).Last();
                Logger.Instance.LogTrace($"Field::StaticGetValueInternal: 0x{getStaticFieldValueInternal.ToInt64().ToString("X2")}");

                var getStaticFieldValueInternalTargets = XrefScannerLowLevel.JumpTargets(getStaticFieldValueInternal).ToArray();

                if (getStaticFieldValueInternalTargets.Length == 0) return FindClassGetFieldDefaultValue(true);

                classGetDefaultFieldValue = getStaticFieldValueInternalTargets.Length == 3 ? getStaticFieldValueInternalTargets.Last() : getStaticFieldValueInternalTargets.First();
            }
            Logger.Instance.LogTrace($"Class::GetDefaultFieldValue: 0x{classGetDefaultFieldValue.ToInt64().ToString("X2")}");

            ClassGetFieldDefaultValueOriginal = NativeUtils.Detour(classGetDefaultFieldValue, ClassGetFieldDefaultValueDetour);
            return Marshal.GetDelegateForFunctionPointer<d_ClassGetFieldDefaultValue>(classGetDefaultFieldValue);
        }
        #endregion

        #region Class::Init
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void d_ClassInit(Il2CppClass* klass);
        internal static d_ClassInit ClassInit;

        private static readonly MemoryUtils.SignatureDefinition[] s_ClassInitSignatures =
        {
            new MemoryUtils.SignatureDefinition
            {
                pattern = "\xE8\x00\x00\x00\x00\x0F\xB7\x47\x28\x83",
                mask = "x????xxxxx",
                xref = true
            },
            new MemoryUtils.SignatureDefinition
            {
                pattern = "\xE8\x00\x00\x00\x00\x0F\xB7\x47\x48\x48",
                mask = "x????xxxxx",
                xref = true
            }
        };

        private static d_ClassInit FindClassInit()
        {
            static nint GetClassInitSubstitute()
            {
                if (TryGetIl2CppExport("mono_class_instance_size", out nint classInit))
                {
                    Logger.Instance.LogTrace($"Picked mono_class_instance_size as a Class::Init substitute");
                    return classInit;
                }
                if (TryGetIl2CppExport("mono_class_setup_vtable", out classInit))
                {
                    Logger.Instance.LogTrace($"Picked mono_class_setup_vtable as a Class::Init substitute");
                    return classInit;
                }
                if (TryGetIl2CppExport(nameof(IL2CPP.il2cpp_class_has_references), out classInit))
                {
                    Logger.Instance.LogTrace($"Picked il2cpp_class_has_references as a Class::Init substitute");
                    return classInit;
                }

                Logger.Instance.LogTrace($"GameAssembly.dll: 0x{Il2CppModule.BaseAddress.ToInt64().ToString("X2")}");
                throw new NotSupportedException("Failed to use signature for Class::Init and a substitute cannot be found, please create an issue and report your unity version & game");
            }
            nint pClassInit = s_ClassInitSignatures
                .Select(s => MemoryUtils.FindSignatureInModule(Il2CppModule, s))
                .FirstOrDefault(p => p != 0);

            if (pClassInit == 0)
            {
                Logger.Instance.LogWarning("Class::Init signatures have been exhausted, using a substitute!");
                pClassInit = GetClassInitSubstitute();
            }

            Logger.Instance.LogTrace($"Class::Init: 0x{pClassInit.ToString("X2")}");

            return Marshal.GetDelegateForFunctionPointer<d_ClassInit>(pClassInit);
        }
        #endregion
    }
}
