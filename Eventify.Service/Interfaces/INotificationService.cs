using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eventify.Service.Interfaces
{
    public interface INotificationService
    {
        Task SendPaymentSuccessAsyncByIntentId(string paymentIntentId);
        Task SendPaymentFailureAsyncByIntentId(string paymentIntentId);
        Task SendRefundNotificationAsyncByIntentId(string paymentIntentId);
        Task SendPaymentSuccessAsync(int bookingId, decimal amount, string email, string phoneNumber = null);
        Task SendPaymentFailureAsync(int bookingId, string email, string phoneNumber = null);
        Task SendRefundNotificationAsync(int bookingId, decimal amount, string email, string phoneNumber = null);
        
        // New methods for Zoom integration
        Task SendEventCreatedNotificationToOrganizerAsync(int eventId);
        Task SendBookingConfirmationWithEventDetailsAsync(int bookingId);
    }
}
