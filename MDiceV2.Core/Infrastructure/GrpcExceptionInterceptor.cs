using System;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;
using MDiceV2.Models;

namespace MDiceV2.Core.Infrastructure;

/// <summary>
/// gRPC 异常拦截器 - 捕获并记录服务方法异常
/// 帮助调试"Exception was thrown by handler"错误
/// </summary>
public class GrpcExceptionInterceptor : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            LogInfo($"[GrpcInterceptor] 收到请求 - 方法: {context.Method}");
            var response = await continuation(request, context);
            LogInfo($"[GrpcInterceptor] 请求成功 - 方法: {context.Method}");
            return response;
        }
        catch (Exception ex)
        {
            LogError($"[GrpcInterceptor] ❌ 异常在方法: {context.Method}");
            LogError($"[GrpcInterceptor] 异常类型: {ex.GetType().FullName}");
            LogError($"[GrpcInterceptor] 异常消息: {ex.Message}");
            LogError($"[GrpcInterceptor] 堆栈跟踪: {ex.StackTrace}");
            
            if (ex.InnerException != null)
            {
                LogError($"[GrpcInterceptor] 内部异常: {ex.InnerException.Message}");
                LogError($"[GrpcInterceptor] 内部堆栈: {ex.InnerException.StackTrace}");
            }
            
            throw;
        }
    }

    private void LogInfo(string message) =>
        LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");

    private void LogError(string message) =>
        LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
}
