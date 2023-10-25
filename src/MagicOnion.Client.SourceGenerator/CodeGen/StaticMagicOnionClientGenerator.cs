using System.Text;
using MagicOnion.Client.SourceGenerator.CodeAnalysis;

namespace MagicOnion.Client.SourceGenerator.CodeGen;

public class StaticMagicOnionClientGenerator
{
    class ServiceClientBuildContext
    {
        public ServiceClientBuildContext(MagicOnionServiceInfo service, StringBuilder writer)
        {
            Service = service;
            Writer = writer;
        }

        public MagicOnionServiceInfo Service { get; }

        public StringBuilder Writer { get; }
    }

    public static string Build(GenerationContext generationContext, MagicOnionServiceInfo serviceInfo)
    {
        using var pooledStringBuilder = generationContext.GetPooledStringBuilder();
        var writer = pooledStringBuilder.Instance;

        EmitHeader(generationContext, writer);

        var buildContext = new ServiceClientBuildContext(serviceInfo, writer);

        EmitPreamble(generationContext, buildContext);
        EmitServiceClientClass(generationContext, buildContext);
        EmitPostscript(generationContext, buildContext);

        return writer.ToString();
    }

    static void EmitHeader(GenerationContext generationContext, StringBuilder writer)
    {
        writer.AppendLine("""
            // <auto-generated />
            #pragma warning disable CS0618 // 'member' is obsolete: 'text'
            #pragma warning disable CS0612 // 'member' is obsolete
            #pragma warning disable CS8019 // Unnecessary using directive.

            """);
    }

    static void EmitPreamble(GenerationContext generationContext, ServiceClientBuildContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(generationContext.Namespace))
        {
            ctx.Writer.AppendLineWithFormat($$"""
            namespace {{generationContext.Namespace}}
            {
            """);
        }
        ctx.Writer.AppendLineWithFormat($$"""
                using global::System;
                using global::Grpc.Core;
                using global::MagicOnion;
                using global::MagicOnion.Client;
                using global::MessagePack;

                partial class {{generationContext.InitializerPartialTypeName}}
                {
                    static partial class MagicOnionGeneratedClient
                    {
            """);
    }

    static void EmitPostscript(GenerationContext generationContext, ServiceClientBuildContext ctx)
    {
        ctx.Writer.AppendLine("""
                    }
                }
            """);

        if (!string.IsNullOrWhiteSpace(generationContext.Namespace))
        {
            ctx.Writer.AppendLine("""
            }
            """);
        }
    }

    static void EmitServiceClientClass(GenerationContext generationContext, ServiceClientBuildContext ctx)
    {
        // [Ignore]
        // public class {ServiceName}Client : MagicOnionClientBase<{ServiceName}>, {ServiceName}
        // {
        //
        ctx.Writer.AppendLineWithFormat($$"""
                        [global::MagicOnion.Ignore]
                        public class {{ctx.Service.GetClientFullName()}} : global::MagicOnion.Client.MagicOnionClientBase<{{ctx.Service.ServiceType.FullName}}>, {{ctx.Service.ServiceType.FullName}}
                        {
            """);
        // class ClientCore { ... }
        EmitClientCore(ctx);
        // private readonly ClientCore core; ...
        EmitFields(ctx);
        // public {ServiceName}Client(MagicOnionClientOptions options, IMagicOnionSerializerProvider serializerProvider) : base(options) { ... }
        // private {ServiceName}Client(MagicOnionClientOptions options, ClientCore core) : base(options) { ... }
        EmitConstructor(ctx);
        // protected override ClientBase<{ServiceName}> Clone(MagicOnionClientOptions options) => new {ServiceName}Client(options, core);
        EmitClone(ctx);
        // public {MethodType}Result<TResponse> MethodName(TArg1 arg1, TArg2 arg2, ...) => this.core.MethodName.Invoke{MethodType}(this, "ServiceName/MethodName", new DynamicArgumentTuple<T1, T2, ...>(arg1, arg2, ...)); ...
        EmitServiceMethods(ctx);

        ctx.Writer.AppendLine("""
                        }
            """);
        // }
    }

    static void EmitClone(ServiceClientBuildContext ctx)
    {
        ctx.Writer.AppendLineWithFormat($"""
                            protected override global::MagicOnion.Client.MagicOnionClientBase<{ctx.Service.ServiceType.FullName}> Clone(global::MagicOnion.Client.MagicOnionClientOptions options)
                                => new {ctx.Service.GetClientFullName()}(options, core);
            """);
        ctx.Writer.AppendLine();
    }

    static void EmitConstructor(ServiceClientBuildContext ctx)
    {
        ctx.Writer.AppendLineWithFormat($$"""
                            public {{ctx.Service.GetClientFullName()}}(global::MagicOnion.Client.MagicOnionClientOptions options, global::MagicOnion.Serialization.IMagicOnionSerializerProvider serializerProvider) : base(options)
                            {
                                this.core = new ClientCore(serializerProvider);
                            }

                            private {{ctx.Service.GetClientFullName()}}(MagicOnionClientOptions options, ClientCore core) : base(options)
                            {
                                this.core = core;
                            }

            """);
    }

    static void EmitFields(ServiceClientBuildContext ctx)
    {
        // private readonly ClientCore core;
        ctx.Writer.AppendLine("""
                            readonly ClientCore core;

            """);
    }

    static void EmitServiceMethods(ServiceClientBuildContext ctx)
    {
        // Implements
        // public UnaryResult<TResponse> MethodName(TArg1 arg1, TArg2 arg2, ...)
        //     => this.core.MethodName.InvokeUnary(this, "ServiceName/MethodName", new DynamicArgumentTuple<T1, T2, ...>(arg1, arg2, ...));
        // public UnaryResult<TResponse> MethodName(TRequest request)
        //     => this.core.MethodName.InvokeUnary(this, "ServiceName/MethodName", request);
        // public UnaryResult<TResponse> MethodName()
        //     => this.core.MethodName.InvokeUnary(this, "ServiceName/MethodName", Nil.Default);
        // public UnaryResult MethodName()
        //     => this.core.MethodName.InvokeUnaryNonGeneric(this, "ServiceName/MethodName", Nil.Default);
        // public Task<ServerStreamingResult<TRequest, TResponse>> MethodName(TArg1 arg1, TArg2 arg2, ...)
        //     => this.core.MethodName.InvokeServerStreaming(this, "ServiceName/MethodName", new DynamicArgumentTuple<T1, T2, ...>(arg1, arg2, ...));
        // public Task<ClientStreamingResult<TRequest, TResponse>> MethodName()
        //     => this.core.MethodName.InvokeClientStreaming(this, "ServiceName/MethodName");
        // public Task<DuplexStreamingResult<TRequest, TResponse>> MethodName()
        //     => this.core.MethodName.InvokeDuplexStreaming(this, "ServiceName/MethodName");
        foreach (var method in ctx.Service.Methods)
        {
            var invokeRequestParameters = method.Parameters.Count switch
            {
                // Invoker for ClientStreaming, DuplexStreaming method has no request parameter.
                _ when (method.MethodType != MethodType.Unary && method.MethodType != MethodType.ServerStreaming) => $"",
                // Nil.Default
                0 => $", global::MessagePack.Nil.Default",
                // arg0
                1 => $", {method.Parameters[0].Name}",
                // new DynamicArgumentTuple(arg1, arg2, ...)
                _ => $", {method.Parameters.ToNewDynamicArgumentTuple()}",
            };
            var hasNonGenericUnaryResult = method.MethodReturnType == MagicOnionTypeInfo.KnownTypes.MagicOnion_UnaryResult;

            ctx.Writer.AppendLineWithFormat($"""
                            public {method.MethodReturnType.FullName} {method.MethodName}({method.Parameters.ToMethodSignaturize()})
                                => this.core.{method.MethodName}.Invoke{method.MethodType}{(hasNonGenericUnaryResult ? "NonGeneric" : "")}(this, "{method.Path}"{invokeRequestParameters});
            """);
        }
    }

    static void EmitClientCore(ServiceClientBuildContext ctx)
    {
        /*
         * class ClientCore
         * {
         *     // UnaryResult<string> HelloAsync(string name, int age);
         *     public UnaryMethodRawInvoker<DynamicArgumentTuple<string, int>, string> HelloAsync;
         *
         *     public ClientCore(IMagicOnionSerializerProvider serializerProvider)
         *     {
         *         this.HelloAsync = UnaryMethodRawInvoker.Create_ValueType_RefType<DynamicArgumentTuple<string, int>, string>("IGreeterService", "HelloAsync", serializerProvider);
         *     }
         * }
         */

        // class ClientCore {
        ctx.Writer.AppendLine("""
                            class ClientCore
                            {
            """);

        // public RawMethodInvoker<TRequest, TResponse> MethodName;
        foreach (var method in ctx.Service.Methods)
        {
            ctx.Writer.AppendLineWithFormat($$"""
                                public global::MagicOnion.Client.Internal.RawMethodInvoker<{{method.RequestType.FullName}}, {{method.ResponseType.FullName}}> {{method.MethodName}};
            """);
        }

        // public ClientCore(IMagicOnionSerializerProvider serializerProvider) {
        ctx.Writer.AppendLine("""
                                public ClientCore(global::MagicOnion.Serialization.IMagicOnionSerializerProvider serializerProvider)
                                {
            """);

        // MethodName = RawMethodInvoker.Create_XXXType_XXXType<TRequest, TResponse>(MethodType, ServiceName, MethodName, serializerProvider);
        foreach (var method in ctx.Service.Methods)
        {
            var createMethodVariant = $"{(method.RequestType.IsValueType ? "Value" : "Ref")}Type_{(method.ResponseType.IsValueType ? "Value" : "Ref")}Type";
            ctx.Writer.AppendLineWithFormat($$"""
                                    this.{{method.MethodName}} = global::MagicOnion.Client.Internal.RawMethodInvoker.Create_{{createMethodVariant}}<{{method.RequestType.FullName}}, {{method.ResponseType.FullName}}>(global::Grpc.Core.MethodType.{{method.MethodType}}, "{{method.ServiceName}}", "{{method.MethodName}}", serializerProvider);
            """);
        }
        ctx.Writer.AppendLine("""
                                }
                             }

            """);
            // }
        // }
    }
}
