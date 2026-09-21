using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoltManager.Setup.Engine
{
    public sealed class SetupStepDiagnostic
    {
        public string Name { get; internal set; } = "";
        public bool Success { get; internal set; }
        public bool Cancelled { get; internal set; }
        public string Message { get; internal set; } = "";
    }

    public sealed class SetupWorkflowResult
    {
        private readonly List<SetupStepDiagnostic> _steps;

        internal SetupWorkflowResult(List<SetupStepDiagnostic> steps, Exception? failure, bool cancelled)
        {
            _steps = steps;
            FailureException = failure;
            Cancelled = cancelled;
        }

        public IReadOnlyList<SetupStepDiagnostic> Steps => _steps;
        public bool Cancelled { get; }
        public bool Success => !Cancelled && FailureException == null && _steps.All(step => step.Success);
        public string? FailedStep => _steps.LastOrDefault(step => !step.Success)?.Name;
        public string Summary => string.Join("; ", _steps.Where(step => !step.Success).Select(step => step.Name + ": " + step.Message));
        internal Exception? FailureException { get; }
    }

    internal sealed class SetupWorkflowStep
    {
        internal SetupWorkflowStep(string name, Func<CancellationToken, Task> execute)
        {
            Name = name;
            Execute = execute;
        }

        internal string Name { get; }
        internal Func<CancellationToken, Task> Execute { get; }
    }

    internal sealed class SetupWorkflowRunner
    {
        internal async Task<SetupWorkflowResult> RunAsync(
            IEnumerable<SetupWorkflowStep> steps,
            CancellationToken cancellationToken)
        {
            var diagnostics = new List<SetupStepDiagnostic>();
            foreach (SetupWorkflowStep step in steps)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await step.Execute(cancellationToken).ConfigureAwait(false);
                    diagnostics.Add(new SetupStepDiagnostic { Name = step.Name, Success = true });
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    diagnostics.Add(new SetupStepDiagnostic
                    {
                        Name = step.Name,
                        Success = false,
                        Cancelled = true,
                        Message = "Operation cancelled",
                    });
                    return new SetupWorkflowResult(diagnostics, ex, cancelled: true);
                }
                catch (Exception ex)
                {
                    diagnostics.Add(new SetupStepDiagnostic
                    {
                        Name = step.Name,
                        Success = false,
                        Message = ex.Message,
                    });
                    return new SetupWorkflowResult(diagnostics, ex, cancelled: false);
                }
            }

            return new SetupWorkflowResult(diagnostics, failure: null, cancelled: false);
        }
    }
}
