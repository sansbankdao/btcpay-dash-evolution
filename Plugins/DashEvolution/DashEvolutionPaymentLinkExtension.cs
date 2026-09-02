// File: Plugins/DashEvolution/DashEvolutionPaymentLinkExtension.cs
//
// Generates the payment URI for the DASHE-CHAIN payment method (the QR code
// payload). Mirrors BitcoinPaymentLinkExtension (which emits a BIP21
// "bitcoin:<addr>?amount=<due>" URI) but for Dash shielded addresses: emits
// "dash:<addr>?amount=<due>". The Dash iOS wallet (dashwallet-ios) registers
// the `dash:` URL scheme (DashPlatform.swift / Info.plist CFBundleURLTypes) and
// can parse the address + amount from it.

#nullable enable
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace BTCPayServer.Plugins.DashEvolution;

public class DashEvolutionPaymentLinkExtension : IPaymentLinkExtension
{
    private const string UriScheme = "dash";
    private readonly PaymentMethodHandlerDictionary _handlers;

    public DashEvolutionPaymentLinkExtension(
        PaymentMethodId paymentMethodId,
        PaymentMethodHandlerDictionary handlers)
    {
        PaymentMethodId = paymentMethodId;
        _handlers = handlers;
    }

    public PaymentMethodId PaymentMethodId { get; }

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        if (string.IsNullOrEmpty(prompt.Destination))
            return null;
        var due = prompt.Calculate().Due;
        // "dash:dash1zrfmyuc8...?amount=0.00619272"
        var uri = $"{UriScheme}:{prompt.Destination}";
        if (due > 0m)
            uri += $"?amount={due.ToString(CultureInfo.InvariantCulture)}";
        return uri;
    }
}
