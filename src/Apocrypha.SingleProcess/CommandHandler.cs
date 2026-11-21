using System.CommandLine.Invocation;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Apocrypha.Abstractions.Cli;
using Apocrypha.Sdk.ProxyConsole;

namespace Apocrypha.SingleProcess;

/// <summary>
/// Command handler for linking a verb definition to the parser
/// </summary>
/// <param name="getters"></param>
/// <param name="methodInfo"></param>
internal class CommandHandler(IServiceProvider serviceProvider, List<Func<InvocationContext, object?>> getters, MethodInfo methodInfo)
    : ICommandHandler
{
    public int Invoke(InvocationContext context)
    {
        throw new NotSupportedException("Only async is supported");
    }

    public async Task<int> InvokeAsync(InvocationContext context)
    {
        try
        {
            // Resolve all the parameters
            var args = GC.AllocateUninitializedArray<object?>(getters.Count);
            for (var i = 0; i < getters.Count; i++)
            {
                args[i] = getters[i](context);
            }

            // Invoke the method
            return await (Task<int>)methodInfo.Invoke(null, args)!;
        }
        catch (Exception ex)
        {
            serviceProvider.GetRequiredService<ILogger<CommandHandler>>().LogWarning("Command {Command} failed ({ErrorType}); arguments omitted", methodInfo.Name, ex.GetType().Name);
            await context.BindingContext.GetRequiredService<IRenderer>().Error(new InvalidOperationException("The command could not be completed. Retry from the running application."), "Command failed");
            return -1;
        }
    }
}
