using AutoMapper;
using Eventify.Core.Entities;
using Eventify.Repository.Data.Contexts;
using Eventify.Repository.Interfaces;
using Eventify.Service.DTOs.Categories;
using Eventify.Service.DTOs.Events;
using Eventify.Service.DTOs.Tickets;
using Eventify.Service.Helpers;
using Eventify.Service.Interfaces;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using SendGrid.Helpers.Errors.Model;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Eventify.Service.Services
{
    public class EventService : IEventService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IMapper _mapper;
        private readonly IPhotoService _photoService;
        private readonly IZoomService _zoomService;
        private readonly INotificationService _notificationService;

        public EventService(IMapper mapper, IUnitOfWork unitOfWork, IPhotoService photoService, IZoomService zoomService, INotificationService notificationService)
        {
            _mapper = mapper;
            _unitOfWork = unitOfWork;
            _photoService = photoService;
            _zoomService = zoomService;
            _notificationService = notificationService;
        }

        public async Task<IEnumerable<EventDto>> GetAllAsync()
        {
            var events = await _unitOfWork._eventRepository.GetAllAsync();
            return _mapper.Map<IEnumerable<EventDto>>(events);
        }

        public async Task<ServiceResult<EventDetailsDto>> GetByIdAsync(int id)
        {
            try
            {
                var eventEntity = await _unitOfWork._eventRepository.GetEventWithDetailsAsync(id);
                if (eventEntity == null)
                {
                    return ServiceResult<EventDetailsDto>.Fail("NotFound", $"Event with ID {id} not found.");
                }

                var eventDetails = _mapper.Map<EventDetailsDto>(eventEntity);
                return ServiceResult<EventDetailsDto>.Ok(eventDetails);
            }
            catch (Exception ex)
            {
                return ServiceResult<EventDetailsDto>.Fail("Exception", $"An error occurred while fetching event details: {ex.Message}");
            }
        }

        public async Task<ServiceResult<EventDto>> CreateAsync(CreateEventDto dto, string id)
        {
            var errors = new List<ValidationError>();
            if (dto.EndDate <= dto.StartDate)
            {
                errors.Add(new ValidationError { Field = "EndDate", Message = "End Date must be after the Start Date" });
            }
            if (dto.StartDate <= DateTime.UtcNow)
            {
                errors.Add(new ValidationError { Field = "StartDate", Message = "Start Date must be in the future" });
            }
            if (errors.Any())
                return ServiceResult<EventDto>.Fail(errors);

            var Categories = JsonConvert.DeserializeObject<List<CategoryCreationByEventDto>>(dto.CategoriesJson);

            using var transaction = await _unitOfWork.BeginTransactionAsync();
            try
            {
                var ev = _mapper.Map<Event>(dto);
                ev.OrganizerID = int.Parse(id);
                if (dto.Photo != null)
                {
                    string photoUrl = await _photoService.UploadPhotoAsync(dto.Photo);
                    ev.PhotoUrl = photoUrl;
                }
                if (dto.IsOnline)
                { 
                    var zoom = await _zoomService.CreateMeeting(
                    dto.Name,
                    dto.StartDate,
                    dto.DurationMinutes ?? 60
                    );
                    if (zoom == null)
                        throw new BadRequestException("Failed to create Zoom meeting. Please try again later.");
                    ev.ZoomJoinUrl = zoom.JoinUrl;
                    ev.ZoomPassword = zoom.Password;
                    ev.ZoomMeetingId = zoom.MeetingId;
                }
                var curEvent = await _unitOfWork._eventRepository.AddAsync(ev);
                await _unitOfWork.SaveChangesAsync();
                var CategoriesToCreate = new List<CreateCategoryDto>();
                var ticketsToCreate = new List<CreateTicketDto>();
                if (Categories is not null && Categories.Any())
                {
                    foreach (var category in Categories)
                    {
                        CategoriesToCreate.Add(new CreateCategoryDto(curEvent.Id, category.Title, category.Seats , category.TicketPrice));
                    }
                }
                var mappedCateories = _mapper.Map<List<Category>>(CategoriesToCreate);
                await _unitOfWork._categoryRepository.AddRangeAsync(mappedCateories);
                await _unitOfWork.SaveChangesAsync();
                foreach (var category in mappedCateories)
                {
                    for (int i = 0; i < category.Seats; i++)
                    {
                        ticketsToCreate.Add(new CreateTicketDto(curEvent.Address, category.Title, category.Id, curEvent.Id , category.TicketPrice));
                    }
                }
                var mappedTickets = _mapper.Map<List<Ticket>>(ticketsToCreate);
                await _unitOfWork._ticketRepository.AddRangeAsync(mappedTickets);
                await _unitOfWork.SaveChangesAsync();
                await transaction.CommitAsync();
                
                // Send event creation notification to organizer
                try
                {
                    await _notificationService.SendEventCreatedNotificationToOrganizerAsync(curEvent.Id);
                }
                catch (Exception ex)
                {
                    // Log but don't fail the event creation
                    // Notification failures shouldn't break the main flow
                }
                var CurDto = _mapper.Map<EventDto>(ev);
                return ServiceResult<EventDto>.Ok(CurDto);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                errors.Add(new ValidationError { Field = "Exception", Message = ex.Message });
                return ServiceResult<EventDto>.Fail(errors);
            }
        }

        public async Task<bool> UpdateAsync(int id, UpdateEventDto dto)
        {
            var ev = await _unitOfWork._eventRepository.GetByIdAsync(id);
            if (ev == null) return false;
            if (dto.Photo != null)
            {
                var uploadResult = await _photoService.UploadPhotoAsync(dto.Photo);

                if (uploadResult == null)
                    throw new Exception("Photo upload failed");

                ev.PhotoUrl = uploadResult; 
            }
            _mapper.Map(dto, ev);
            _unitOfWork._eventRepository.Update(ev);
            await _unitOfWork.SaveChangesAsync();
            return true;
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var ev = await _unitOfWork._eventRepository.GetByIdAsync(id);
            if (ev == null) return false;
            _unitOfWork._eventRepository.Delete(ev);
            await _unitOfWork.SaveChangesAsync();
            return true;
        }
        public async Task<ServiceResult<IEnumerable<EventDto>>> GetUpcomingEventsAsync(int take = 10)
        {
            try
            {
                var upcomingEvents = await _unitOfWork._eventRepository.GetUpcommingAsync(take);

                if (!upcomingEvents.Any())
                {
                    return ServiceResult<IEnumerable<EventDto>>.Fail("NoEvents", "No upcoming events found.");
                }

                var eventDtos = _mapper.Map<IEnumerable<EventDto>>(upcomingEvents);
                return ServiceResult<IEnumerable<EventDto>>.Ok(eventDtos);
            }
            catch (Exception ex)
            {
                return ServiceResult<IEnumerable<EventDto>>.Fail("Exception", $"An error occurred while fetching upcoming events: {ex.Message}");
            }
        }

        public async Task<ServiceResult<IEnumerable<EventDto>>> GetEventsByOrganizerAsync(int organizerId)
        {
            try
            {
                var allEvents = await _unitOfWork._eventRepository.GetAllAsync();
                var organizerEvents = allEvents
                    .Where(e => e.OrganizerID == organizerId)
                    .OrderByDescending(e => e.StartDate)
                    .ToList();

                if (!organizerEvents.Any())
                {
                    return ServiceResult<IEnumerable<EventDto>>.Fail("NoEvents", $"No events found for organizer with ID {organizerId}.");
                }
                var eventDtos = _mapper.Map<IEnumerable<EventDto>>(organizerEvents).ToList();
                
                // Get all payments with their bookings and tickets
                var allPayments = await _unitOfWork._paymentRepository.GetAllAsync();
                
                foreach (var e in eventDtos) 
                {
                    e.BookedTickets = _unitOfWork._ticketRepository.CountBookedTickets(e.Id);
                    
                    // Calculate revenue from paid bookings for this event
                    // Get all tickets for this event
                    var eventTickets = organizerEvents
                        .FirstOrDefault(evt => evt.Id == e.Id)?.Tickets ?? new List<Ticket>();
                    
                    // Get booking IDs from those tickets
                    var bookingIds = eventTickets
                        .Where(t => t.BookingId.HasValue)
                        .Select(t => t.BookingId.Value)
                        .Distinct()
                        .ToList();
                    
                    // Sum revenue from paid payments for these bookings
                    e.Revenue = allPayments
                        .Where(p => bookingIds.Contains(p.BookingId) && p.Status == Core.Enums.PaymentStatus.Paid)
                        .Sum(p => p.TotalPrice);
                }
                return ServiceResult<IEnumerable<EventDto>>.Ok(eventDtos);
            }
            catch (Exception ex)
            {
                return ServiceResult<IEnumerable<EventDto>>.Fail("Exception", $"An error occurred while fetching organizer events: {ex.Message}");
            }
        }

        public async Task<ServiceResult<IEnumerable<EventDto>>> SearchEventsAsync(string searchTerm)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(searchTerm))
                {
                    return ServiceResult<IEnumerable<EventDto>>.Fail("InvalidSearch", "Search term cannot be empty.");
                }

                var allEvents = await _unitOfWork._eventRepository.GetAllAsync();
                var matchingEvents = allEvents
                    .Where(e => e.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                               e.Description.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                               e.Address.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                    .Where(e => e.StartDate > DateTime.UtcNow) 
                    .OrderBy(e => e.StartDate)
                    .ToList();

                if (!matchingEvents.Any())
                {
                    return ServiceResult<IEnumerable<EventDto>>.Fail("NoResults", $"No events found matching '{searchTerm}'.");
                }

                var eventDtos = _mapper.Map<IEnumerable<EventDto>>(matchingEvents);
                return ServiceResult<IEnumerable<EventDto>>.Ok(eventDtos);
            }
            catch (Exception ex)
            {
                return ServiceResult<IEnumerable<EventDto>>.Fail("Exception", $"An error occurred during search: {ex.Message}");
            }
        }
    }
}
