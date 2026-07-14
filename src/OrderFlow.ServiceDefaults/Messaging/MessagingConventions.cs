using System.Collections.Concurrent;
using System.Text;
using OrderFlow.Contracts.Messages;

namespace OrderFlow.ServiceDefaults.Messaging;

/// <summary>
/// The naming rule that ties OrderFlow.Contracts to the queues and topics the AppHost declares.
/// Producers resolve it to pick a destination; consumers resolve it to pick a subscription. Both
/// must agree, so it lives in exactly one place.
/// </summary>
public static class MessagingConventions
{
    private static readonly ConcurrentDictionary<Type, string> EntityNames = new();

    /// <summary>The Service Bus entity a message type belongs to: <c>ReserveInventory</c> → <c>reserve-inventory</c>.</summary>
    public static string EntityNameFor(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);

        return EntityNames.GetOrAdd(messageType, static type => ToKebabCase(type.Name));
    }

    /// <inheritdoc cref="EntityNameFor(Type)"/>
    public static string EntityNameFor<TMessage>() where TMessage : MessageBase
        => EntityNameFor(typeof(TMessage));

    private static string ToKebabCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var character = name[i];

            if (char.IsUpper(character))
            {
                if (i > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
