using OrderFlow.Payments.API.Managers.Domain;
using OrderFlow.Payments.API.Managers.ServiceModels;

namespace OrderFlow.Payments.API.Managers.Extensions;

/// <summary>
/// Domain → ServiceModel. Hand-rolled assignments only — no AutoMapper, no Mapster, no reflection.
/// </summary>
/// <remarks>
/// The one place the authorization code is allowed to change shape on its way out. A convention-based
/// mapper would have copied it verbatim, because that is what convention-based mappers do: they
/// propagate every field they can match, including the ones you did not mean to publish.
/// </remarks>
public static class PaymentMappingExtensions
{
    private const int VisibleAuthorizationCodeCharacters = 4;

    public static PaymentServiceModel ToServiceModel(this Payment payment) => new()
    {
        Id = payment.Id,
        OrderId = payment.OrderId,
        Amount = payment.Amount,
        Status = payment.Status.ToString(),
        IsAuthorized = payment.Status is PaymentStatus.Captured or PaymentStatus.Refunded,
        DeclineReason = payment.DeclineReason,
        AuthorizationCodeMasked = Mask(payment.AuthorizationCode),
        CreatedUtc = payment.CreatedUtc,
        UpdatedUtc = payment.UpdatedUtc

        // IdempotencyKey is not mapped. See PaymentServiceModel.
    };

    public static IReadOnlyList<PaymentServiceModel> ToServiceModels(this IEnumerable<Payment> payments) =>
        [.. payments.Select(ToServiceModel)];

    /// <summary>AUTH-1A2B3C4D → AUTH-****3C4D. Enough to reconcile a charge, not enough to be one.</summary>
    /// <remarks>
    /// The AUTH- prefix is kept because it is a format marker, not a secret — starring it would make
    /// the value unrecognisable in the ops view without hiding anything that was not already public.
    /// </remarks>
    private static string Mask(string authorizationCode)
    {
        if (string.IsNullOrEmpty(authorizationCode))
        {
            return string.Empty;
        }

        const string prefix = "AUTH-";

        return authorizationCode.StartsWith(prefix, StringComparison.Ordinal)
            ? $"{prefix}{MaskBody(authorizationCode[prefix.Length..])}"
            : MaskBody(authorizationCode);
    }

    private static string MaskBody(string body)
    {
        if (body.Length <= VisibleAuthorizationCodeCharacters)
        {
            // Too short to mask meaningfully. Reveal nothing rather than most of it.
            return new string('*', body.Length);
        }

        var visible = body[^VisibleAuthorizationCodeCharacters..];
        var hidden = new string('*', body.Length - VisibleAuthorizationCodeCharacters);

        return $"{hidden}{visible}";
    }
}
