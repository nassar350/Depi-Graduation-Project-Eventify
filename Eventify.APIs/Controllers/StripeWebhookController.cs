using Eventify.Core.Enums;
using Eventify.Repository.Interfaces;
using Eventify.Service.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Stripe;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Threading.Tasks;

namespace Eventify.APIs.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class StripeWebhookController : ControllerBase
    {
        private readonly IPaymentService _paymentService;
        private readonly INotificationService _notificationService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<StripeWebhookController> _logger;

        public StripeWebhookController(
            IPaymentService paymentService,
            INotificationService notificationService,
            IConfiguration configuration,
            ILogger<StripeWebhookController> logger)
        {
            _paymentService = paymentService;
            _notificationService = notificationService;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> HandleWebhook()
        {
            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

            var endpointSecret = Environment.GetEnvironmentVariable("WebhookSecret");

            if (string.IsNullOrEmpty(endpointSecret))
                throw new Exception("Stripe Webhook Key not set!");

            Event stripeEvent;

            try
            {
                var signatureHeader = Request.Headers["Stripe-Signature"];
                stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, endpointSecret);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Webhook signature verification failed");
                return BadRequest($"Webhook signature verification failed: {ex.Message}");
            }

            try
            {
                switch (stripeEvent.Type)
                {
                    case "payment_intent.succeeded":
                        var succeededIntent = stripeEvent.Data.Object as PaymentIntent;
                        await _paymentService.UpdatePaymentStatusAsync(succeededIntent.Id, PaymentStatus.Paid);
                        await _notificationService.SendPaymentSuccessAsyncByIntentId(succeededIntent.Id);

                        // Also send booking confirmation with event details (includes Zoom if online)
                        var payment = await _paymentService.GetByIntentIdAsync(succeededIntent.Id);
                        if (payment != null)
                        {
                            try
                            {
                                await _notificationService.SendBookingConfirmationWithEventDetailsAsync(payment.BookingId);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Failed to send booking confirmation for booking {payment.BookingId}");
                            }
                        }
                        break;

                    case "payment_intent.payment_failed":
                        var failedIntent = stripeEvent.Data.Object as PaymentIntent;
                        await _paymentService.UpdatePaymentStatusAsync(failedIntent.Id, PaymentStatus.Rejected);
                        await _notificationService.SendPaymentFailureAsyncByIntentId(failedIntent.Id);
                        break;

                    case "payment_intent.canceled":
                        var canceledIntent = stripeEvent.Data.Object as PaymentIntent;
                        await _paymentService.UpdatePaymentStatusAsync(canceledIntent.Id, PaymentStatus.Cancelled);
                        break;

                    case "charge.refunded":
                        var charge = stripeEvent.Data.Object as Charge;
                        await _paymentService.UpdateRefundStatusAsync(charge.PaymentIntentId, PaymentStatus.Refunded);
                        await _notificationService.SendRefundNotificationAsyncByIntentId(charge.PaymentIntentId);
                        break;

                    default:
                        _logger.LogInformation($"Unhandled Stripe event type: {stripeEvent.Type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Stripe webhook");
                return StatusCode(500);
            }

            return Ok();
        }
    }
}
