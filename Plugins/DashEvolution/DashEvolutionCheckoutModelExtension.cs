// File: Plugins/DashEvolution/DashEvolutionCheckoutModelExtension.cs
//
// ICheckoutModelExtension for DASHE-CHAIN. Without this, the checkout page's
// Vue <component :is="paymentMethodComponent"> resolves to null (because
// CheckoutBodyComponentName is never set) and NO address / QR code renders —
// the customer sees only "Amount Due" with no destination. This extension
// sets CheckoutBodyComponentName to "DashEvolutionCheckoutBody" (a Vue
// component registered by Views/DashEvolutionMethodCheckout.cshtml) and
// populates InvoiceBitcoinUrl / InvoiceBitcoinUrlQR with the dash: payment
// URI (via DashEvolutionPaymentLinkExtension) so the QR code encodes a
// scannable payment link.
//
// Mirrors BitcoinCheckoutModelExtension (which sets CheckoutBodyComponentName
// = "BitcoinCheckoutBody" + InvoiceBitcoinUrl from BitcoinPaymentLinkExtension)
// but without Bitcoin-specific concerns (PayJoin, LN fallback, bech32 upper-
// casing for BTC, sats display mode, confirmation tracking).

#nullable enable
using BTCPayServer.Payments;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.DashEvolution;

public class DashEvolutionCheckoutModelExtension : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "DashEvolutionCheckoutBody";

    private readonly BTCPayNetworkBase _network;
    private readonly IPaymentLinkExtension _paymentLinkExtension;

    public DashEvolutionCheckoutModelExtension(
        PaymentMethodId paymentMethodId,
        BTCPayNetworkBase network,
        IPaymentLinkExtension paymentLinkExtension)
    {
        PaymentMethodId = paymentMethodId;
        _network = network;
        _paymentLinkExtension = paymentLinkExtension;
    }

    public string Image => _network.CryptoImagePath;
    public string Badge => "";
    public PaymentMethodId PaymentMethodId { get; }

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        var prompt = context.Prompt;
        if (context.Handler.ParsePaymentPromptDetails(prompt.Details)
            is not DashEvolutionPaymentPromptDetails details)
            return;

        context.Model.CheckoutBodyComponentName = CheckoutBodyComponentName;

        // The QR code encodes "dash:<address>?amount=<due>" — scannable by
        // the Dash iOS wallet. InvoiceBitcoinUrl (clickable "Pay in wallet"
        // link) = same URI. InvoiceBitcoinUrlQR = same (no bech32 upper-casing
        // needed; Dash shielded addresses are case-insensitive bech32m and
        // the wallet accepts lowercase).
        var link = _paymentLinkExtension.GetPaymentLink(prompt, context.UrlHelper);
        context.Model.InvoiceBitcoinUrl = link ?? "";
        context.Model.InvoiceBitcoinUrlQR = link ?? "";

        // Transport tabs (Transparent | Shielded): expose BOTH addresses +
        // their dash: payment links via the model's JsonExtensionData bag so
        // the checkout component can switch between them. shieldedAddress is
        // always the Orchard address from the prompt details (model.address
        // is the prompt Destination = the DEFAULT tab's address, which may
        // be the Transparent address when both transports are unique).
        context.Model.AdditionalData["shieldedAddress"] = details.Address;
        context.Model.AdditionalData["shieldedPaymentLink"] = BuildPaymentLink(details.Address, prompt);
        if (details.TransparentAddress is not null)
        {
            context.Model.AdditionalData["transparentAddress"] = details.TransparentAddress;
            context.Model.AdditionalData["transparentPaymentLink"] = BuildPaymentLink(details.TransparentAddress, prompt);
        }
    }

    // Same URI shape as DashEvolutionPaymentLinkExtension (dash:<addr>?amount=<due>),
    // built per transport address (the registered link extension only covers
    // the prompt Destination).
    private static string BuildPaymentLink(string address, PaymentPrompt prompt)
    {
        var due = prompt.Calculate().Due;
        var uri = $"dash:{address}";
        if (due > 0m)
            uri += $"?amount={due.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return uri;
    }
}
