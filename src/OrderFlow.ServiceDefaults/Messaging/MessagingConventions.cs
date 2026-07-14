using System.Collections.Concurrent;
using System.Security.Cryptography;
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

    /// <summary>
    /// A MessageId derived from what the message MEANS rather than when it was created: the same
    /// (correlation, discriminator) pair always yields the same id.
    /// </summary>
    /// <remarks>
    /// This is what makes a retry safe. Redeliver a command and the sender re-emits its reply — with
    /// a random id that reply looks brand new to the receiver's <c>(ConsumerName, MessageId)</c>
    /// guard and gets handled twice. Derived from the order id and the message name, the duplicate
    /// is recognised and dropped. Use the message type name as the discriminator unless one order
    /// can legitimately produce two of the same message.
    /// </remarks>
    public static Guid DeterministicMessageId(Guid correlationId, string discriminator)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{correlationId:N}:{discriminator}"));

        return new Guid(hash.AsSpan(0, 16));
    }

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
