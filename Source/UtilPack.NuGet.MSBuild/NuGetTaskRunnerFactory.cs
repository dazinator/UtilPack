﻿/*
 * Copyright 2017 Stanislav Muhametsin. All rights Reserved.
 *
 * Licensed  under the  Apache License,  Version 2.0  (the "License");
 * you may not use  this file  except in  compliance with the License.
 * You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed  under the  License is distributed on an "AS IS" BASIS,
 * WITHOUT  WARRANTIES OR CONDITIONS  OF ANY KIND, either  express  or
 * implied.
 *
 * See the License for the specific language governing permissions and
 * limitations under the License. 
 */
using Microsoft.Build.Framework;
using NuGet.Common;
using NuGet.Repositories;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml.Linq;
using NuGet.Versioning;
using System.Reflection;
using NuGet.Frameworks;

using TPropertyInfo = System.ValueTuple<UtilPack.NuGet.MSBuild.WrappedPropertyKind, UtilPack.NuGet.MSBuild.WrappedPropertyInfo, System.Reflection.PropertyInfo>;
using TNuGetPackageResolverCallback = System.Func<System.String, System.String, System.String, System.Threading.Tasks.Task<System.Reflection.Assembly>>;
using TNuGetPackagesResolverCallback = System.Func<System.String[], System.String[], System.String[], System.Threading.Tasks.Task<System.Reflection.Assembly[]>>;
using TAssemblyByPathResolverCallback = System.Func<System.String, System.Reflection.Assembly>;
using System.Collections.Concurrent;
using NuGet.Configuration;
using NuGet.ProjectModel;
using NuGet.Commands;
using NuGet.Protocol.Core.Types;
using NuGet.Packaging;
using NuGet.LibraryModel;

using TResolveResult = System.Collections.Generic.IDictionary<System.String, UtilPack.NuGet.MSBuild.ResolvedPackageInfo>;
using System.Reflection.Emit;
using System.Threading;

using TTaskTypeGenerationParameters = System.ValueTuple<System.Boolean, System.Collections.Generic.IDictionary<System.String, System.ValueTuple<UtilPack.NuGet.MSBuild.WrappedPropertyKind, UtilPack.NuGet.MSBuild.WrappedPropertyInfo>>>;
using TTaskInstanceCreationInfo = System.ValueTuple<UtilPack.NuGet.MSBuild.TaskReferenceHolder, UtilPack.NuGet.MSBuild.ResolverLogger>;
using System.Threading.Tasks;
using UtilPack.NuGet;
using UtilPack.NuGet.MSBuild;
using UtilPack;
using UtilPack.NuGet.AssemblyLoading;
using UtilPack.NuGet.Common.MSBuild;

namespace UtilPack.NuGet.MSBuild
{
   // On first task create the task type is dynamically generated, and app domain initialized
   // On cleanup, app domain will be unloaded, but task type kept
   // On subsequent uses, app-domain will be re-initialized and unloaded again, but the generated type prevails.
   public partial class NuGetTaskRunnerFactory : ITaskFactory
   {

      private sealed class TaskReferenceHolderInfo : IDisposable
      {
         private readonly Lazy<IDictionary<String, (WrappedPropertyKind, WrappedPropertyInfo)>> _propertyInfo;
         private readonly Action _dispose;

         public TaskReferenceHolderInfo(
            TaskReferenceHolder taskRef,
            ResolverLogger resolverLogger,
            Action dispose
            )
         {
            this.TaskReference = taskRef;
            this.Logger = resolverLogger;
            this._dispose = dispose;
            this._propertyInfo = new Lazy<IDictionary<string, (WrappedPropertyKind, WrappedPropertyInfo)>>( () => taskRef.GetPropertyInfo().ToDictionary( kvp => kvp.Key, kvp => TaskReferenceHolder.DecodeKindAndInfo( kvp.Value ) ) );
         }

         public TaskReferenceHolder TaskReference { get; }

         public ResolverLogger Logger { get; }

         public IDictionary<String, (WrappedPropertyKind, WrappedPropertyInfo)> PropertyInfo => this._propertyInfo.Value;

         public void Dispose()
         {
            this._dispose?.Invoke();
         }
      }

      private const String PACKAGE_ID = "PackageID";
      private const String PACKAGE_VERSION = "PackageVersion";
      private const String ASSEMBLY_PATH = "AssemblyPath";
      private const String NUGET_FW = "NuGetFramework";
      private const String NUGET_FW_VERSION = "NuGetFrameworkVersion";
      private const String NUGET_FW_PACKAGE_ID = "NuGetFrameworkPackageID";
      private const String NUGET_FW_PACKAGE_VERSION = "NuGetFrameworkPackageVersion";
      //private const String KNOWN_SDK_PACKAGE = "KnownSDKPackage";

      // We will re-create anything that needs re-creating between mutiple task usages from this same lazy.
      private ReadOnlyResettableLazy<TaskReferenceHolderInfo> _helper;

      // We will generate task type only exactly once, no matter how many times the actual task is created.
      private readonly Lazy<Type> _taskType;

      // Logger for this task factory
      private IBuildEngine _logger;

      public NuGetTaskRunnerFactory()
      {
         this._taskType = new Lazy<Type>( () => GenerateTaskType( (this._helper.Value.TaskReference.IsCancelable, this._helper.Value.PropertyInfo) ) );
      }

      public String FactoryName => nameof( NuGetTaskRunnerFactory );

      public Type TaskType
      {
         get
         {
            return this._taskType.Value;
         }
      }

      public void CleanupTask( ITask task )
      {
         if ( this._helper.IsValueCreated && this._helper.Value.TaskReference.TaskUsesDynamicLoading )
         {
            // In .NET Desktop, task factory logger seems to become invalid almost immediately after initialize method, so...
            // Don't log.

            //this._logger.LogMessageEvent( new BuildMessageEventArgs(
            //   "Cleaning up task since it was detected to be using dynamic loading.",
            //   null,
            //   this.FactoryName,
            //   MessageImportance.Normal,
            //   DateTime.UtcNow
            //   ) );

            // Reset tasks that do dynamic NuGet package assembly loading
            // On .NET Desktop, this will cause app domain unload
            // On .NET Core, this will cause assembly load context to be disposed
            this._helper.Value.DisposeSafely();
            this._helper.Reset();
         }
      }

      public ITask CreateTask(
         IBuildEngine taskFactoryLoggingHost
         )
      {
         return (ITask) this._taskType.Value.GetConstructors()[0].Invoke( new Object[] { this._helper.Value.TaskReference, this._helper.Value.Logger } );
      }

      public TaskPropertyInfo[] GetTaskParameters()
      {
         return this._helper.Value.PropertyInfo
            .Select( kvp =>
            {
               var propType = GetPropertyType( kvp.Value.Item1 );
               var info = kvp.Value.Item2;
               return propType == null ?
                  null :
                  new Microsoft.Build.Framework.TaskPropertyInfo( kvp.Key, propType, info == WrappedPropertyInfo.Out, info == WrappedPropertyInfo.Required );
            } )
            .Where( propInfo => propInfo != null )
            .ToArray();
      }

      public Boolean Initialize(
         String taskName,
         IDictionary<String, TaskPropertyInfo> parameterGroup,
         String taskBody,
         IBuildEngine taskFactoryLoggingHost
         )
      {
         var retVal = false;
         try
         {
            this._logger = taskFactoryLoggingHost;

            var taskBodyElement = XElement.Parse( taskBody );

            // Nuget stuff
            var thisFW = UtilPackNuGetUtility.TryAutoDetectThisProcessFramework( (taskBodyElement.ElementAnyNS( NUGET_FW )?.Value, taskBodyElement.ElementAnyNS( NUGET_FW_VERSION )?.Value) );

            var nugetSettings = UtilPackNuGetUtility.GetNuGetSettingsWithDefaultRootDirectory(
               Path.GetDirectoryName( taskFactoryLoggingHost.ProjectFileOfTaskNode ),
               taskBodyElement.ElementAnyNS( "NuGetConfigurationFile" )?.Value
               );
            var nugetLogger = new NuGetMSBuildLogger(
               "NR0001",
               "NR0002",
               this.FactoryName,
               this.FactoryName,
               taskFactoryLoggingHost
               );
            var nugetResolver = new BoundRestoreCommandUser(
               nugetSettings,
               thisFramework: thisFW,
               nugetLogger: nugetLogger
               );


            // Restore task package
            // TODO cancellation token source + cancel on Ctrl-C (since Inititalize method offers no support for asynchrony/cancellation)
            String packageID;
            var restoreResult = nugetResolver.RestoreIfNeeded(
               ( packageID = taskBodyElement.ElementAnyNS( PACKAGE_ID )?.Value ),
               taskBodyElement.ElementAnyNS( PACKAGE_VERSION )?.Value
               ).GetAwaiter().GetResult();
            String packageVersion;
            if ( restoreResult != null
               && !String.IsNullOrEmpty( ( packageVersion = restoreResult.Libraries.Where( lib => String.Equals( lib.Name, packageID ) ).FirstOrDefault()?.Version?.ToNormalizedString() ) )
               )
            {
               GetFileItemsDelegate getFiles = ( rid, lib, libs ) => GetSuitableFiles( thisFW, rid, lib, libs );
               // On Desktop we must always load everything, since it's possible to have assemblies compiled against .NET Standard having references to e.g. System.IO.FileSystem.dll, which is not present in GAC
#if NET45
               String[] sdkPackages = null;
#else

               var sdkPackageID = thisFW.GetSDKPackageID( taskBodyElement.ElementAnyNS( NUGET_FW_PACKAGE_ID )?.Value );
               var sdkRestoreResult = nugetResolver.RestoreIfNeeded(
                     sdkPackageID,
                     thisFW.GetSDKPackageVersion( sdkPackageID, taskBodyElement.ElementAnyNS( NUGET_FW_PACKAGE_VERSION )?.Value )
                     ).GetAwaiter().GetResult();
               var sdkPackages = sdkRestoreResult.Libraries.Select( lib => lib.Name ).ToArray();
#endif

               var taskAssemblies = nugetResolver.ExtractAssemblyPaths(
                  restoreResult,
                  getFiles,
                  sdkPackages
                  )[packageID];
               var assemblyPath = UtilPackNuGetUtility.GetAssemblyPathFromNuGetAssemblies(
                  taskAssemblies.Assemblies,
                  taskBodyElement.ElementAnyNS( ASSEMBLY_PATH )?.Value,
                  ap => File.Exists( ap )
                  );
               if ( !String.IsNullOrEmpty( assemblyPath ) )
               {
                  taskName = this.ProcessTaskName( taskBodyElement, taskName );
                  this._helper = LazyFactory.NewReadOnlyResettableLazy( () =>
                  {
                     try
                     {
                        var tempFolder = Path.Combine( Path.GetTempPath(), "NuGetAssemblies_" + packageID + "_" + packageVersion + "_" + ( Guid.NewGuid().ToString() ) );
                        Directory.CreateDirectory( tempFolder );

                        return this.CreateExecutionHelper(
                           taskName,
                           taskBodyElement,
                           packageID,
                           packageVersion,
                           assemblyPath,
                           nugetResolver,
                           new ResolverLogger( nugetLogger ),
                           getFiles,
                           tempFolder
#if !NET45
                     , sdkRestoreResult
#endif
                   );
                     }
                     catch ( Exception exc )
                     {
                        Console.Error.WriteLine( "Exception when creating task: " + exc );
                        throw;
                     }
                  }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication );
                  retVal = true;
               }
               else
               {
                  taskFactoryLoggingHost.LogErrorEvent(
                     new BuildErrorEventArgs(
                        "Task factory error",
                        "NMSBT003",
                        null,
                        -1,
                        -1,
                        -1,
                        -1,
                        $"Failed to find suitable assembly in {packageID}@{packageVersion}.",
                        null,
                        this.FactoryName
                     )
                  );
               }
            }
            else
            {
               taskFactoryLoggingHost.LogErrorEvent(
                  new BuildErrorEventArgs(
                     "Task factory error",
                     "NMSBT002",
                     null,
                     -1,
                     -1,
                     -1,
                     -1,
                     $"Failed to find main package, check that you have suitable {PACKAGE_ID} element in task body and that package is installed.",
                     null,
                     this.FactoryName
                  )
               );
            }
         }
         catch ( Exception exc )
         {
            taskFactoryLoggingHost.LogErrorEvent( new BuildErrorEventArgs(
               "Task factory error",
               "NMSBT003",
               null,
               -1,
               -1,
               -1,
               -1,
               $"Exception in initialization: {exc}",
               null,
               this.FactoryName
               ) );
         }
         return retVal;
      }

      private static IEnumerable<String> GetSuitableFiles(
         NuGetFramework thisFramework,
         String runtimeIdentifier,
         LockFileTargetLibrary targetLibrary,
         Lazy<IDictionary<String, LockFileLibrary>> libraries
         )
      {
         var retVal = UtilPackNuGetUtility.GetRuntimeAssembliesDelegate( runtimeIdentifier, targetLibrary, libraries );
         if ( !retVal.Any() && libraries.Value.TryGetValue( targetLibrary.Name, out var lib ) )
         {

            // targetLibrary does not list stuff like build/net45/someassembly.dll
            // So let's do manual matching
            var fwGroups = lib.Files.Where( f =>
            {
               return f.StartsWith( PackagingConstants.Folders.Build, StringComparison.OrdinalIgnoreCase )
                      && PackageHelper.IsAssembly( f )
                      && Path.GetDirectoryName( f ).Length > PackagingConstants.Folders.Build.Length + 1;
            } ).GroupBy( f =>
            {
               try
               {
                  return NuGetFramework.ParseFolder( f.Split( '/' )[1] );
               }
               catch
               {
                  return null;
               }
            } )
           .Where( g => g.Key != null )
           .Select( g => new FrameworkSpecificGroup( g.Key, g ) );

            var matchingGroup = NuGetFrameworkUtility.GetNearest(
               fwGroups,
               thisFramework,
               g => g.TargetFramework );
            retVal = matchingGroup?.Items;
         }

         return retVal;
      }

      private String ProcessTaskName(
         XElement taskBodyElement,
         String taskName
         )
      {
         var overrideTaskName = taskBodyElement.ElementAnyNS( "TaskName" )?.Value;
         return String.IsNullOrEmpty( overrideTaskName ) ? taskName : taskName;
      }

      internal static void RegisterToResolverEvents(
         NuGetAssemblyResolver resolver,
         ResolverLogger logger
         )
      {
         resolver.OnAssemblyLoadSuccess += args => logger.Log( $"Resolved {args.AssemblyName} located in {args.OriginalPath} and loaded from {args.ActualPath}." );
         resolver.OnAssemblyLoadFail += args => logger.Log( $"Failed to resolve {args.AssemblyName}." );
      }

      private static Func<String, String> CreatePathProcessor( String assemblyCopyTargetFolder )
      {
         return originalPath =>
         {
            var newPath = Path.Combine( assemblyCopyTargetFolder, Path.GetFileName( originalPath ) );
            File.Copy( originalPath, newPath, true );
            return newPath;
         };
      }

      private static Type GenerateTaskType( TTaskTypeGenerationParameters parameters )
      {
         // Since we are executing task in different app domain, our task type must inherit MarshalByRefObject
         // However, we don't want to impose such restriction to task writers - ideal situation would be for task writer to only target .netstandard 1.3 (or .netstandard1.4+ and .net45+, but we still don't want to make such restriction).
         // Furthermore, tasks which only target .netstandard 1.3 don't even have MarshalByRefObject.
         // So, let's generate our own dynamic task type.

         // We should load the actual task type in different domain and collect all public properties with getter and setter.
         // Then, we generate type with same property names, but property types should be Either String or ITaskItem[].
         // All getter and setter logic is forwarded by this generated type to our TaskReferenceHolder class, inheriting MarshalByRefObject and residing in actual task's AppDomain.
         // The TaskReferenceHolder will take care of converting required stuff.

         // public class NuGetTaskWrapper : ITask
         // {
         //    private readonly TaskReferenceHolder _task;
         //
         //    public String SomeProperty
         //    {
         //       get
         //       {
         //           return this._task.GetProperty("SomeProperty");
         //       }
         //       set
         //       {
         //           this._task.SetProperty("SomeProperty", value);
         //       }
         //     }
         //     ...
         // }

         var isCancelable = parameters.Item1;
         var propertyInfos = parameters.Item2;

         var ab = AssemblyBuilder.DefineDynamicAssembly( new AssemblyName( "NuGetTaskWrapperDynamicAssembly" ), AssemblyBuilderAccess.RunAndCollect );
         var mb = ab.DefineDynamicModule( "NuGetTaskWrapperDynamicAssembly.dll"
#if NET45
               , false
#endif
               );
         var tb = mb.DefineType( "NuGetTaskWrapper", TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Public );
         tb.AddInterfaceImplementation( typeof( ITask ) );

         var taskField = tb.DefineField( "_task", typeof( TaskReferenceHolder ), FieldAttributes.Private | FieldAttributes.InitOnly );
         var loggerField = tb.DefineField( "_logger", typeof( ResolverLogger ), FieldAttributes.Private | FieldAttributes.InitOnly );

         // Constructor
         var ctor = tb.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig,
            CallingConventions.HasThis,
            new Type[] { typeof( TaskReferenceHolder ), typeof( ResolverLogger ) }
            );
         var il = ctor.GetILGenerator();
         il.Emit( OpCodes.Ldarg_0 );
         il.Emit( OpCodes.Call, typeof( Object ).GetConstructor( new Type[] { } ) );

         il.Emit( OpCodes.Ldarg_0 );
         il.Emit( OpCodes.Ldarg_1 );
         il.Emit( OpCodes.Stfld, taskField );

         il.Emit( OpCodes.Ldarg_0 );
         il.Emit( OpCodes.Ldarg_2 );
         il.Emit( OpCodes.Stfld, loggerField );

         il.Emit( OpCodes.Ret );
         // Properties
         var taskRefGetter = typeof( TaskReferenceHolder ).GetMethod( nameof( TaskReferenceHolder.GetProperty ) );
         var taskRefSetter = typeof( TaskReferenceHolder ).GetMethod( nameof( TaskReferenceHolder.SetProperty ) );
         var toStringCall = typeof( Convert ).GetMethod( nameof( Convert.ToString ), new Type[] { typeof( Object ) } );
         var requiredAttribute = typeof( RequiredAttribute ).GetConstructor( new Type[] { } );
         var outAttribute = typeof( OutputAttribute ).GetConstructor( new Type[] { } );
         var beSetter = typeof( ResolverLogger ).GetMethod( nameof( ResolverLogger.TaskBuildEngineSet ) );
         var beReady = typeof( ResolverLogger ).GetMethod( nameof( ResolverLogger.TaskBuildEngineIsReady ) );
         if ( taskRefGetter == null )
         {
            throw new Exception( "Internal error: no property getter." );
         }
         else if ( taskRefSetter == null )
         {
            throw new Exception( "Internal error: no property getter." );
         }
         else if ( toStringCall == null )
         {
            throw new Exception( "Internal error: no Convert.ToString." );
         }
         else if ( requiredAttribute == null )
         {
            throw new Exception( "Internal error: no Required attribute constructor." );
         }
         else if ( outAttribute == null )
         {
            throw new Exception( "Internal error: no Out attribute constructor." );
         }
         else if ( beSetter == null )
         {
            throw new Exception( "Internal error: no log setter." );
         }
         else if ( beReady == null )
         {
            throw new Exception( "Internal error: no log state updater." );
         }

         var outPropertyInfos = new List<(String, WrappedPropertyKind, Type, FieldBuilder)>();
         void EmitPropertyConversionCode( ILGenerator curIL, WrappedPropertyKind curKind, Type curPropType )
         {
            if ( curKind != WrappedPropertyKind.StringNoConversion )
            {
               // Emit conversion
               if ( curKind == WrappedPropertyKind.String )
               {
                  // Call to Convert.ToString
                  il.Emit( OpCodes.Call, toStringCall );
               }
               else
               {
                  // Just cast
                  il.Emit( OpCodes.Castclass, curPropType );
               }
            }
         }
         foreach ( var kvp in propertyInfos )
         {
            (var kind, var info) = kvp.Value;
            var propType = GetPropertyType( kind );
            if ( propType == null )
            {
               switch ( kind )
               {
                  case WrappedPropertyKind.BuildEngine:
                     propType = typeof( IBuildEngine );
                     break;
                  case WrappedPropertyKind.TaskHost:
                     propType = typeof( ITaskHost );
                     break;
                  default:
                     throw new Exception( $"Property handling code has changed, unknown wrapped property kind: {kind}." );
               }
            }

            var methodAttributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig;
            if ( kind == WrappedPropertyKind.BuildEngine || kind == WrappedPropertyKind.TaskHost )
            {
               // Virtual is required for class methods implementing interface methods
               methodAttributes |= MethodAttributes.Virtual;
            }

            var getter = tb.DefineMethod(
               "get_" + kvp.Key,
               methodAttributes
               );
            getter.SetReturnType( propType );
            il = getter.GetILGenerator();

            if ( info == WrappedPropertyInfo.Out )
            {
               var outField = tb.DefineField( "_out" + outPropertyInfos.Count, propType, FieldAttributes.Private );
               il.Emit( OpCodes.Ldarg_0 );
               il.Emit( OpCodes.Ldfld, outField );
               outPropertyInfos.Add( (kvp.Key, kind, propType, outField) );
            }
            else
            {
               il.Emit( OpCodes.Ldarg_0 );
               il.Emit( OpCodes.Ldfld, taskField );
               il.Emit( OpCodes.Ldstr, kvp.Key );
               il.Emit( OpCodes.Callvirt, taskRefGetter );
               EmitPropertyConversionCode( il, kind, propType );
            }
            il.Emit( OpCodes.Ret );

            MethodBuilder setter;
            if ( info == WrappedPropertyInfo.Out )
            {
               setter = null;
            }
            else
            {
               setter = tb.DefineMethod(
                  "set_" + kvp.Key,
                  methodAttributes
                  );
               setter.SetParameters( new Type[] { propType } );
               il = setter.GetILGenerator();
               if ( kind == WrappedPropertyKind.BuildEngine )
               {
                  // Update the logger
                  il.Emit( OpCodes.Ldarg_0 );
                  il.Emit( OpCodes.Ldfld, loggerField );
                  il.Emit( OpCodes.Ldarg_1 );
                  il.Emit( OpCodes.Callvirt, beSetter );
               }

               il.Emit( OpCodes.Ldarg_0 );
               il.Emit( OpCodes.Ldfld, taskField );
               il.Emit( OpCodes.Ldstr, kvp.Key );
               il.Emit( OpCodes.Ldarg_1 );
               il.Emit( OpCodes.Callvirt, taskRefSetter );
               il.Emit( OpCodes.Ret );
            }
            var prop = tb.DefineProperty(
               kvp.Key,
               PropertyAttributes.None,
               propType,
               new Type[] { }
               );
            prop.SetGetMethod( getter );
            if ( setter != null )
            {
               prop.SetSetMethod( setter );
            }

            switch ( info )
            {
               case WrappedPropertyInfo.Required:
                  prop.SetCustomAttribute( new CustomAttributeBuilder( requiredAttribute, new object[] { } ) );
                  break;
               case WrappedPropertyInfo.Out:
                  prop.SetCustomAttribute( new CustomAttributeBuilder( outAttribute, new object[] { } ) );
                  break;
            }
         }
         // Execute method
         var execute = tb.DefineMethod(
            nameof( Microsoft.Build.Framework.ITask.Execute ),
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            typeof( Boolean ),
            new Type[] { }
            );
         il = execute.GetILGenerator();
         il.Emit( OpCodes.Ldarg_0 );
         il.Emit( OpCodes.Ldfld, loggerField );
         il.Emit( OpCodes.Callvirt, beReady );

         if ( outPropertyInfos.Count > 0 )
         {
            // try { return this._task.Execute(); } finally { this.OutProperty = this._task.GetProperty( "Out" ); }
            var retValLocal = il.DeclareLocal( typeof( Boolean ) );
            il.Emit( OpCodes.Ldc_I4_0 );
            il.Emit( OpCodes.Stloc, retValLocal );
            il.BeginExceptionBlock();
            il.Emit( OpCodes.Ldarg_0 );
            il.Emit( OpCodes.Ldfld, taskField );
            il.Emit( OpCodes.Callvirt, typeof( TaskReferenceHolder ).GetMethod( nameof( TaskReferenceHolder.Execute ) ) );
            il.Emit( OpCodes.Stloc, retValLocal );
            il.BeginFinallyBlock();
            foreach ( var outSetter in outPropertyInfos )
            {
               il.Emit( OpCodes.Ldarg_0 );
               il.Emit( OpCodes.Ldarg_0 );
               il.Emit( OpCodes.Ldfld, taskField );
               il.Emit( OpCodes.Ldstr, outSetter.Item1 );
               il.Emit( OpCodes.Callvirt, taskRefGetter );
               EmitPropertyConversionCode( il, outSetter.Item2, outSetter.Item3 );
               il.Emit( OpCodes.Stfld, outSetter.Item4 );
            }
            il.EndExceptionBlock();

            il.Emit( OpCodes.Ldloc, retValLocal );
         }
         else
         {
            // return this._task.Execute();
            il.Emit( OpCodes.Ldarg_0 );
            il.Emit( OpCodes.Ldfld, taskField );
            il.Emit( OpCodes.Tailcall );
            il.Emit( OpCodes.Callvirt, typeof( TaskReferenceHolder ).GetMethod( nameof( TaskReferenceHolder.Execute ) ) );
         }
         il.Emit( OpCodes.Ret );

         // Canceability
         if ( isCancelable )
         {
            tb.AddInterfaceImplementation( typeof( Microsoft.Build.Framework.ICancelableTask ) );
            var cancel = tb.DefineMethod(
               nameof( Microsoft.Build.Framework.ICancelableTask.Cancel ),
               MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
               typeof( void ),
               new Type[] { }
               );
            var cancelMethod = typeof( TaskReferenceHolder ).GetMethod( nameof( TaskReferenceHolder.Cancel ) );
            if ( cancelMethod == null )
            {
               throw new Exception( "Internal error: no cancel." );
            }
            il = cancel.GetILGenerator();
            // Call cancel to TaskReferenceHolder which will forward it to actual task
            il.Emit( OpCodes.Ldarg_0 );
            il.Emit( OpCodes.Ldfld, taskField );
            il.Emit( OpCodes.Tailcall );
            il.Emit( OpCodes.Callvirt, cancelMethod );
            il.Emit( OpCodes.Ret );
         }

         // We are ready
         return tb.
#if NET45
            CreateType()
#else
            CreateTypeInfo().AsType()
#endif
            ;



      }

      private static Type GetPropertyType( WrappedPropertyKind kind )
      {
         switch ( kind )
         {
            case WrappedPropertyKind.String:
            case WrappedPropertyKind.StringNoConversion:
               return typeof( String );
            case WrappedPropertyKind.TaskItem:
               return typeof( Microsoft.Build.Framework.ITaskItem[] );
            case WrappedPropertyKind.TaskItem2:
               return typeof( Microsoft.Build.Framework.ITaskItem2[] );
            default:
               return null;
         }
      }

      internal static void LoadTaskType(
         String taskTypeName,
         NuGetAssemblyResolver resolver,
         String packageID,
         String packageVersion,
         String assemblyPath,
         out ConstructorInfo taskConstructor,
         out Object[] constructorArguments,
         out Boolean usesDynamicLoading
         )
      {
         // This should never cause any actual async waiting, since LockFile for task package has been already cached by restorer
         var taskAssembly = resolver.LoadNuGetAssembly( packageID, packageVersion, assemblyPath: assemblyPath ).GetAwaiter().GetResult();
         var taskType = taskAssembly.GetType( taskTypeName, true, false );
         if ( taskType == null )
         {
            throw new Exception( $"Could not find task with type {taskTypeName} from assembly {taskAssembly}." );
         }
         GetTaskConstructorInfo( resolver, taskType, out taskConstructor, out constructorArguments );
         usesDynamicLoading = ( constructorArguments?.Length ?? 0 ) > 0;
      }

      private static void GetTaskConstructorInfo(
         NuGetAssemblyResolver resolver,
         Type type,
         out ConstructorInfo matchingCtor,
         out Object[] ctorParams
         )
      {
         var ctors = type
#if !NET45
            .GetTypeInfo()
#endif
            .GetConstructors();
         matchingCtor = null;
         ctorParams = null;
         if ( ctors.Length > 0 )
         {
            var ctorInfo = new Dictionary<Int32, IDictionary<ISet<Type>, ConstructorInfo>>();
            foreach ( var ctor in ctors )
            {
               var paramz = ctor.GetParameters();
               ctorInfo
                  .GetOrAdd_NotThreadSafe( paramz.Length, pl => new Dictionary<ISet<Type>, ConstructorInfo>( SetEqualityComparer<Type>.DefaultEqualityComparer ) )
                  .Add( new HashSet<Type>( paramz.Select( p => p.ParameterType ) ), ctor );
            }

            TNuGetPackageResolverCallback nugetResolveCallback = ( packageID, packageVersion, assemblyPath ) => resolver.LoadNuGetAssembly( packageID, packageVersion, assemblyPath: assemblyPath );
            TNuGetPackagesResolverCallback nugetsResolveCallback = ( packageIDs, packageVersions, assemblyPaths ) => resolver.LoadNuGetAssemblies( packageIDs, packageVersions, assemblyPaths );
            TAssemblyByPathResolverCallback pathResolveCallback = ( assemblyPath ) => resolver.LoadOtherAssembly( assemblyPath );

            if (
               ctorInfo.TryGetValue( 3, out var curInfo )
               && curInfo.TryGetValue( new HashSet<Type>() { typeof( TNuGetPackageResolverCallback ), typeof( TNuGetPackagesResolverCallback ), typeof( TAssemblyByPathResolverCallback ) }, out matchingCtor )
               )
            {
               ctorParams = new Object[3];
               ctorParams[Array.FindIndex( matchingCtor.GetParameters(), p => p.ParameterType.Equals( typeof( TNuGetPackageResolverCallback ) ) )] = nugetResolveCallback;
               ctorParams[Array.FindIndex( matchingCtor.GetParameters(), p => p.ParameterType.Equals( typeof( TNuGetPackagesResolverCallback ) ) )] = nugetsResolveCallback;
               ctorParams[Array.FindIndex( matchingCtor.GetParameters(), p => p.ParameterType.Equals( typeof( TAssemblyByPathResolverCallback ) ) )] = pathResolveCallback;
            }

            if (
               matchingCtor == null
               && ctorInfo.TryGetValue( 2, out curInfo )
               )
            {
               if ( curInfo.TryGetValue( new HashSet<Type>() { typeof( TNuGetPackagesResolverCallback ), typeof( TNuGetPackageResolverCallback ) }, out matchingCtor )
                  || curInfo.TryGetValue( new HashSet<Type>() { typeof( TNuGetPackagesResolverCallback ), typeof( TAssemblyByPathResolverCallback ) }, out matchingCtor )
                  || curInfo.TryGetValue( new HashSet<Type>() { typeof( TNuGetPackageResolverCallback ), typeof( TAssemblyByPathResolverCallback ) }, out matchingCtor ) )
               {
                  ctorParams = new Object[2];
                  Int32 idx;
                  if ( ( idx = Array.FindIndex( matchingCtor.GetParameters(), p => p.ParameterType.Equals( typeof( TNuGetPackagesResolverCallback ) ) ) ) >= 0 )
                  {
                     ctorParams[idx] = nugetsResolveCallback;
                  }
                  if ( ( idx = Array.FindIndex( matchingCtor.GetParameters(), p => p.ParameterType.Equals( typeof( TNuGetPackageResolverCallback ) ) ) ) >= 0 )
                  {
                     ctorParams[idx] = nugetResolveCallback;
                  }
                  if ( ( idx = Array.FindIndex( matchingCtor.GetParameters(), p => p.ParameterType.Equals( typeof( TAssemblyByPathResolverCallback ) ) ) ) >= 0 )
                  {
                     ctorParams[idx] = pathResolveCallback;
                  }
               }
            }

            if (
               matchingCtor == null
               && ctorInfo.TryGetValue( 1, out curInfo )
               )
            {
               if ( curInfo.TryGetValue( new HashSet<Type>( typeof( TNuGetPackagesResolverCallback ).Singleton() ), out matchingCtor ) )
               {
                  ctorParams = new Object[] { nugetsResolveCallback };
               }
               else if ( curInfo.TryGetValue( new HashSet<Type>( typeof( TNuGetPackageResolverCallback ).Singleton() ), out matchingCtor ) )
               {
                  ctorParams = new Object[] { nugetResolveCallback };
               }
               else if ( curInfo.TryGetValue( new HashSet<Type>( typeof( TAssemblyByPathResolverCallback ).Singleton() ), out matchingCtor ) )
               {
                  ctorParams = new Object[] { pathResolveCallback };
               }
            }

            if (
               matchingCtor == null
               && ctorInfo.TryGetValue( 0, out curInfo )
               )
            {
               matchingCtor = curInfo.Values.First();
            }
         }

         if ( matchingCtor == null )
         {
            throw new Exception( $"No public suitable constructors found for type {type.AssemblyQualifiedName}." );
         }

      }

      private static Boolean IsMBFAssembly( AssemblyName an )
      {
         switch ( an.Name )
         {
            case "Microsoft.Build":
            case "Microsoft.Build.Framework":
            case "Microsoft.Build.Tasks.Core":
            case "Microsoft.Build.Utilities.Core":
               return true;
            default:
               return false;
         }
      }
   }

   // Instances of this class reside in target task app domain, so we must be careful not to use any UtilPack stuff here! So no ArgumentValidator. etc.
   public sealed class TaskReferenceHolder
#if NET45
      : MarshalByRefObject
#endif
   {
      private sealed class TaskPropertyInfo
      {
         public TaskPropertyInfo(
            WrappedPropertyKind wrappedPropertyKind,
            WrappedPropertyInfo wrappedPropertyInfo,
            Func<Object> getter,
            Action<Object> setter,
            Func<String, Object> converter
            )
         {
            this.WrappedPropertyKind = wrappedPropertyKind;
            this.WrappedPropertyInfo = wrappedPropertyInfo;
            this.Getter = getter;
            this.Setter = setter;
            this.Converter = converter;
         }

         public WrappedPropertyKind WrappedPropertyKind { get; }
         public WrappedPropertyInfo WrappedPropertyInfo { get; }
         public Func<Object> Getter { get; }
         public Action<Object> Setter { get; }
         public Func<String, Object> Converter { get; }
      }

      private readonly Object _task;
      private readonly MethodInfo _executeMethod;
      private readonly MethodInfo _cancelMethod;
      private readonly IDictionary<String, TaskPropertyInfo> _propertyInfos;

      public TaskReferenceHolder( Object task, String msbuildFrameworkAssemblyName, Boolean taskUsesDynamicLoading )
      {
         this._task = task ?? throw new Exception( "Failed to create the task object." );
         this.TaskUsesDynamicLoading = taskUsesDynamicLoading;
         var mbfInterfaces = this._task.GetType().GetInterfaces()
            .Where( iFace => iFace
#if !NET45
            .GetTypeInfo()
#endif
            .Assembly.GetName().FullName.Equals( msbuildFrameworkAssemblyName ) )
            .ToArray();
         // TODO explicit implementations
         this._executeMethod = mbfInterfaces
            .Where( iFace => iFace.FullName.Equals( CommonHelpers.MBF + nameof( Microsoft.Build.Framework.ITask ) ) )
            .First().GetMethods().First( m => m.Name.Equals( nameof( Microsoft.Build.Framework.ITask.Execute ) ) && m.GetParameters().Length == 0 && m.ReturnType.FullName.Equals( typeof( Boolean ).FullName ) );
         this._cancelMethod = mbfInterfaces
            .FirstOrDefault( iFace => iFace.FullName.Equals( CommonHelpers.MBF + nameof( Microsoft.Build.Framework.ICancelableTask ) ) )
            ?.GetMethods()?.First( m => m.Name.Equals( nameof( Microsoft.Build.Framework.ICancelableTask.Cancel ) ) && m.GetParameters().Length == 0 );

         this._propertyInfos = CommonHelpers.GetPropertyInfoFromType(
            task.GetType(),
            new AssemblyName( msbuildFrameworkAssemblyName )
            ).ToDictionary(
               kvp => kvp.Key,
               kvp =>
               {
                  var curProperty = kvp.Value.Item3;
                  var propType = curProperty.PropertyType;
                  var converter = kvp.Value.Item1 == WrappedPropertyKind.String ?
                     ( propType.GetTypeInfo().IsEnum ? (Func<String, Object>) ( str => Enum.Parse( propType, str, true ) ) : ( str => Convert.ChangeType( str, propType ) ) ) :
                     (Func<String, Object>) null;
                  return new TaskPropertyInfo(
                     kvp.Value.Item1,
                     kvp.Value.Item2,
                     () => curProperty.GetMethod.Invoke( this._task, null ),
                     val => curProperty.SetMethod.Invoke( this._task, new[] { val } ),
                     converter
                  );
               } );

      }

      // Passing value tuples thru appdomain boundaries is errorprone, so just use normal integers here
      internal IDictionary<String, Int32> GetPropertyInfo()
      {
         return this._propertyInfos.ToDictionary( kvp => kvp.Key, kvp => EncodeKindAndInfo( kvp.Value.WrappedPropertyKind, kvp.Value.WrappedPropertyInfo ) );
      }

      internal Boolean IsCancelable => this._cancelMethod != null;

      internal Boolean TaskUsesDynamicLoading { get; }

      // Called by generated task type
      public void Cancel()
      {
         this._cancelMethod.Invoke( this._task, null );
      }

      // Called by generated task type
      public Object GetProperty( String propertyName )
      {
         return this._propertyInfos.TryGetValue( propertyName, out var info ) ?
            info.Getter() :
            throw new InvalidOperationException( $"Property \"{propertyName}\" is not supported for this task." );
      }

      // Called by generated task type
      public void SetProperty( String propertyName, Object value )
      {
         if ( this._propertyInfos.TryGetValue( propertyName, out var info ) )
         {
            if ( info.Converter != null )
            {
               value = info.Converter( (String) value );
            }
            info.Setter( value );
         }
         else
         {
            throw new InvalidOperationException( $"Property \"{propertyName}\" is not supported for this task." );
         }
      }

      // Called by generated task type
      public Boolean Execute()
      {
         // We can't cast to Microsoft.Build.Framework.ITask, since the 14.0 version will be loaded (from GAC), if target task assembly is netstandard assembly.
         // This is because this project depends on msbuild 14.3 in net45 build.

         // So... just invoke dynamically.
         return (Boolean) this._executeMethod.Invoke( this._task, null );
      }

      internal static Int32 EncodeKindAndInfo( WrappedPropertyKind kind, WrappedPropertyInfo info )
      {
         // 3 lowest bits to info and all upper bits to kind
         return ( ( (Int32) kind ) << 3 ) | ( ( (Int32) info ) & 0x03 );
      }

      internal static (WrappedPropertyKind, WrappedPropertyInfo) DecodeKindAndInfo( Int32 encoded )
      {
         return ((WrappedPropertyKind) ( ( encoded & 0xF8 ) >> 3 ), (WrappedPropertyInfo) ( ( encoded & 0x03 ) ));

      }

   }

   // Instances of this class reside in task factory app domain.
   // Has to be public, since it is used by dynamically generated task type.
   public sealed class ResolverLogger
#if NET45
      : MarshalByRefObject
#endif
   {
      private const Int32 INITIAL = 0;
      private const Int32 TASK_BE_INITIALIZING = 1;
      private const Int32 TASK_BE_READY = 2;

      private IBuildEngine _be;
      private Int32 _state;
      private readonly List<String> _queuedMessages;
      private readonly NuGetMSBuildLogger _nugetLogger;

      internal ResolverLogger( NuGetMSBuildLogger nugetLogger )
      {
         this._queuedMessages = new List<String>();
         this._nugetLogger = nugetLogger;
      }

      // This is called by generated task type in its IBuildEngine setter
      public void TaskBuildEngineSet( IBuildEngine be )
      {
         if ( be != null && Interlocked.CompareExchange( ref this._state, TASK_BE_INITIALIZING, INITIAL ) == INITIAL )
         {
            Interlocked.Exchange( ref this._be, be );
            this._nugetLogger.BuildEngine = null;
         }
      }

      // This is called by generated task type in its Execute method start
      public void TaskBuildEngineIsReady()
      {
         if ( Interlocked.CompareExchange( ref this._state, TASK_BE_READY, TASK_BE_INITIALIZING ) == TASK_BE_INITIALIZING )
         {
            this._nugetLogger.BuildEngine = this._be;
            // process all queued messages
            foreach ( var msg in this._queuedMessages )
            {
               this.Log( msg );
            }
            this._queuedMessages.Clear();
         }
      }

      public void Log( String message )
      {
         switch ( this._state )
         {
            case TASK_BE_READY:
               this._be.LogMessageEvent( new BuildMessageEventArgs(
                  message,
                  null,
                  "NuGetPackageAssemblyResolver",
                  MessageImportance.Low,
                  DateTime.UtcNow,
                  null
               ) );
               break;
            default:
               // When assembly resolve happens during task initialization (setting BuildEngine etc properties).
               // Using BuildEngine then will cause NullReferenceException as its LoggingContext property is not yet set.
               // And task factory logging context has already been marked inactive, so this is when we can't immediately log.
               // In this case, just queue message, and log them once task's Execute method has been invoked.
               this._queuedMessages.Add( message );
               break;
         }

      }
   }



#if NET45
   [Serializable] // We want to be serializable instead of MarshalByRef as we want to copy these objects
#endif
   internal sealed class ResolvedPackageInfo
   {
      public ResolvedPackageInfo( String packageDirectory, String[] assemblies )
      {
         this.PackageDirectory = packageDirectory;
         this.Assemblies = assemblies;
      }

      public String PackageDirectory { get; }
      public String[] Assemblies { get; }
   }

   // These methods are used by both .net45 and .netstandard.
   // This class has no implemented interfaces and extends System.Object.
   // Therefore using this static method from another appdomain won't cause any assembly resolves.
   internal static class CommonHelpers
   {
      internal const String MBF = "Microsoft.Build.Framework.";

      public static String GetAssemblyPathFromNuGetAssemblies(
         String[] assemblyPaths,
         String packageExpandedPath,
         String optionalGivenAssemblyPath
         )
      {
         String assemblyPath = null;
         if ( assemblyPaths.Length == 1 || (
               assemblyPaths.Length > 1 // There is more than 1 possible assembly
               && !String.IsNullOrEmpty( ( assemblyPath = optionalGivenAssemblyPath ) ) // AssemblyPath task property was given
               && ( assemblyPath = Path.GetFullPath( ( Path.Combine( packageExpandedPath, assemblyPath ) ) ) ).StartsWith( packageExpandedPath ) // The given assembly path truly resides in the package folder
               ) )
         {
            // TODO maybe check that assembly path is in possibleAssemblies array?
            if ( assemblyPath == null )
            {
               assemblyPath = assemblyPaths[0];
            }
         }
         return assemblyPath;
      }

      public static IDictionary<String, TPropertyInfo> GetPropertyInfoFromType(
         Type type,
         AssemblyName msbuildFrameworkAssemblyName
         )
      {
         // Doing typeof( Microsoft.Build.Framework.ITask ).Assembly.GetName().FullName; will cause MSBuild 14.0 assembly to be loaded in net45 build, if target assembly is .netstandard assembly.
         // This most likely due the fact that net45 build requires msbuild 14.X (msbuild 15.X requires net46).
         // So, just get the msbuildTaskAssemblyName from original appdomain as a parameter to this method.
         // That is why MBF string consts & other helper constructs exist, and why we can't cast stuff directly to Microsoft.Build.Framework types.


         var retVal = new Dictionary<String, TPropertyInfo>();
         foreach ( var property in type.GetRuntimeProperties().Where( p => ( p.GetMethod?.IsPublic ?? false ) && ( p.SetMethod?.IsPublic ?? false ) ) )
         {
            var curProperty = property;
            var propertyType = curProperty.PropertyType;
            var actualType = propertyType;
            if ( actualType.IsArray )
            {
               actualType = actualType.GetElementType();
            }
            WrappedPropertyKind? kind;
            switch ( Type.GetTypeCode( actualType ) )
            {
               case TypeCode.Object:
                  if ( ISMFBType( actualType, msbuildFrameworkAssemblyName ) )
                  {
                     if ( Equals( actualType.FullName, MBF + nameof( Microsoft.Build.Framework.IBuildEngine ) ) )
                     {
                        kind = WrappedPropertyKind.BuildEngine;
                     }
                     else if ( Equals( actualType.FullName, MBF + nameof( Microsoft.Build.Framework.ITaskHost ) ) )
                     {
                        kind = WrappedPropertyKind.TaskHost;
                     }
                     else if ( Equals( actualType.FullName, MBF + nameof( Microsoft.Build.Framework.ITaskItem ) ) )
                     {
                        kind = WrappedPropertyKind.TaskItem;
                     }
                     else if ( Equals( actualType.FullName, MBF + nameof( Microsoft.Build.Framework.ITaskItem2 ) ) )
                     {
                        kind = WrappedPropertyKind.TaskItem2;
                     }
                     else
                     {
                        kind = null;
                     }
                  }
                  else
                  {
                     kind = null;
                  }
                  break;
#if NET45
               case TypeCode.DBNull:
#endif
               case TypeCode.Empty:
                  kind = null;
                  break;
               case TypeCode.String:
                  kind = WrappedPropertyKind.StringNoConversion;
                  break;
               default:
                  kind = WrappedPropertyKind.String;
                  break;
            }

            if ( kind.HasValue )
            {
               WrappedPropertyInfo info;
               var customMBFAttrs = curProperty.GetCustomAttributes( true )
                  .Where( ca => ISMFBType( ca.GetType(), msbuildFrameworkAssemblyName ) )
                  .ToArray();
               if ( customMBFAttrs.Any( ca => Equals( ca.GetType().FullName, MBF + nameof( Microsoft.Build.Framework.RequiredAttribute ) ) ) )
               {
                  info = WrappedPropertyInfo.Required;
               }
               else if ( customMBFAttrs.Any( ca => Equals( ca.GetType().FullName, MBF + nameof( Microsoft.Build.Framework.OutputAttribute ) ) ) )
               {
                  info = WrappedPropertyInfo.Out;
               }
               else
               {
                  info = WrappedPropertyInfo.None;
               }

               retVal.Add( curProperty.Name, (kind.Value, info, curProperty) );
            }
         }

         return retVal;
      }

      private static Boolean ISMFBType( Type type, AssemblyName mfbAssembly )
      {
         var an = type
#if !NET45
                     .GetTypeInfo()
#endif
                     .Assembly.GetName();
         Byte[] pk;
         return String.Equals( an.Name, mfbAssembly.Name )
            && ( pk = an.GetPublicKeyToken() ) != null
            && mfbAssembly.GetPublicKeyToken().SequenceEqual( pk );
      }
   }

   internal enum WrappedPropertyKind
   {
      String,
      StringNoConversion,
      TaskItem,
      TaskItem2,
      BuildEngine,
      TaskHost
   }

   internal enum WrappedPropertyInfo
   {
      None,
      Required,
      Out
   }

}

public static partial class E_UtilPack
{
   // From https://stackoverflow.com/questions/1145659/ignore-namespaces-in-linq-to-xml
   internal static IEnumerable<XElement> ElementsAnyNS<T>( this IEnumerable<T> source, String localName )
      where T : XContainer
   {
      return source.Elements().Where( e => e.Name.LocalName == localName );
   }

   internal static XElement ElementAnyNS<T>( this IEnumerable<T> source, String localName )
      where T : XContainer
   {
      return source.ElementsAnyNS( localName ).FirstOrDefault();
   }

   internal static IEnumerable<XElement> ElementsAnyNS( this XContainer source, String localName )
   {
      return source.Elements().Where( e => e.Name.LocalName == localName );
   }

   internal static XElement ElementAnyNS( this XContainer source, String localName )
   {
      return source.ElementsAnyNS( localName ).FirstOrDefault();
   }
}