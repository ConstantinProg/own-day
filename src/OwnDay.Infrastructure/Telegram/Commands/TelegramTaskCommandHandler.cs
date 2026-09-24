using System.Globalization;
using System.Text;
using OwnDay.Application.Interactions;
using OwnDay.Application.Tasks;
using OwnDay.Domain.Tasks;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramTaskCommandHandler(TaskService taskService)
{
    private const int MaximumMessageLength = 4096;

    public bool Handles(string name) => name is "add" or "tasks" or "done";

    public async Task<IReadOnlyList<string>> HandleAsync(
        ProcessIncomingCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Name is "tasks")
        {
            return await ListAsync(command, cancellationToken);
        }

        return [await HandleSingleAsync(command, cancellationToken)];
    }

    private Task<string> HandleSingleAsync(ProcessIncomingCommand command, CancellationToken cancellationToken) =>
        command.Name switch
        {
            "add" => AddAsync(command, cancellationToken),
            "done" => CompleteAsync(command, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

    private async Task<string> AddAsync(ProcessIncomingCommand command, CancellationToken cancellationToken)
    {
        var title = command.Arguments.Trim();
        if (title.Length is 0)
        {
            return "Usage: /add <title>";
        }

        if (title.Length > TaskItem.MaxTitleLength)
        {
            return $"Task title must be at most {TaskItem.MaxTitleLength} characters.";
        }

        var task = await taskService.AddAsync(command.UserId, title, cancellationToken);
        return $"Task #{task.Id} added: {task.Title}";
    }

    private async Task<IReadOnlyList<string>> ListAsync(
        ProcessIncomingCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Arguments.Length > 0)
        {
            return ["Usage: /tasks"];
        }

        var tasks = await taskService.GetActiveAsync(command.UserId, cancellationToken);
        if (tasks.Count is 0)
        {
            return ["No active tasks."];
        }

        var messages = new List<string>();
        var current = new StringBuilder();
        foreach (var task in tasks)
        {
            var line = $"#{task.Id} {task.Title}";
            if (current.Length > 0 && current.Length + 1 + line.Length > MaximumMessageLength)
            {
                messages.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(line);
        }

        messages.Add(current.ToString());
        return messages;
    }

    private async Task<string> CompleteAsync(ProcessIncomingCommand command, CancellationToken cancellationToken)
    {
        if (!long.TryParse(command.Arguments, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return "Usage: /done <task-id>";
        }

        var result = await taskService.CompleteAsync(command.UserId, id, cancellationToken);
        return result switch
        {
            CompleteTaskResult.Completed => $"Task #{id} completed.",
            CompleteTaskResult.AlreadyCompleted => $"Task #{id} is already completed.",
            _ => "Task not found."
        };
    }
}
