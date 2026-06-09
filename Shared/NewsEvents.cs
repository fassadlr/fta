using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Gui.Tools;

namespace NinjaTrader.NinjaScript.Strategies.FTA.Shared
{
    public class NewsEvent(DateTime eventTime, bool allDay = false)
    {
        public readonly DateTime EventTime = eventTime;
        public bool AllDay { get; set; } = allDay;
        public bool BankHoliday { get; set; } = false;
        public bool IsNFP { get; private set; }
        public bool IsCPI { get; private set; }
        public bool IsFOMC { get; private set; }

        public static NewsEvent CreateAllDay(DateTime eventTime)
        {
            return new NewsEvent(eventTime, true);
        }

        public static NewsEvent CreateUSBankHoliday(DateTime eventTime)
        {
            return new NewsEvent(eventTime, false) { BankHoliday = true };
        }

        public static NewsEvent CreateNFPEvent(DateTime eventTime)
        {
            return new NewsEvent(eventTime) { IsNFP = true };
        }

        public static NewsEvent CreateCPIEvent(DateTime eventTime)
        {
            return new NewsEvent(eventTime) { IsCPI = true };
        }

        public static NewsEvent CreateFOMCEvent(DateTime eventTime)
        {
            return new NewsEvent(eventTime) { IsFOMC = true };
        }
    }

    public class NewsFilter(bool includeCPI, bool includeFOMC, bool includeNFP, bool includeNoEvents)
    {
        public bool IncludeCPI { get; } = includeCPI;
        public bool IncludeFOMC { get; } = includeFOMC;
        public bool IncludeNFP { get; } = includeNFP;
        public bool IncludeNoEvents { get; } = includeNoEvents;
    }

    public static class NewsEvents
    {
        public static List<NewsEvent> Events()
        {
            var events = new List<NewsEvent>() {

                // May 2024
                NewsEvent.CreateAllDay(new DateTime(2024, 04, 01)), // Easter Monday
                NewsEvent.CreateNFPEvent(new DateTime(2024, 04, 05, 08, 30, 00)),    // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2024, 04, 10, 08, 30, 00)),    // CPI

                // May 2024
                NewsEvent.CreateFOMCEvent(new DateTime(2024, 05, 01, 19, 19, 00)),   // FOMC
                NewsEvent.CreateNFPEvent(new DateTime(2024, 05, 03, 08, 30, 00)),    // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2024, 05, 15, 08, 30, 00)),    // CPI

                // June 2024
                NewsEvent.CreateNFPEvent(new DateTime(2024, 06, 07, 08, 30, 00)),    // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2024, 06, 12, 08, 30, 00)),    // CPI
                NewsEvent.CreateFOMCEvent(new DateTime(2024, 06, 12, 19, 00, 00)),   // FOMC

                // July 2024
                NewsEvent.CreateNFPEvent(new DateTime(2024, 07, 05, 08, 30, 00)),    // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2024, 07, 11, 08, 30, 00)),    // CPI
                NewsEvent.CreateFOMCEvent(new DateTime(2024, 07, 31, 19, 00, 00)),   // FOMC

                // September 2024
                NewsEvent.CreateNFPEvent(new DateTime(2024, 09, 06,08, 30, 00)),    // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2024, 09, 11, 08, 30, 00)),    // CPI
                NewsEvent.CreateFOMCEvent(new DateTime(2024, 09, 18, 19, 00, 00)),   // FOMC

                // October 2024
                NewsEvent.CreateNFPEvent(new DateTime(2024, 10, 04, 08, 30, 00)),    // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2024, 10, 10, 08, 30, 00)),    // CPI

                // November 2024
                NewsEvent.CreateNFPEvent(new DateTime(2024, 11, 01, 08, 30, 00)),    // NFP
                NewsEvent.CreateFOMCEvent(new DateTime(2024, 11, 07, 19, 00, 00)),   // FOMC
                NewsEvent.CreateCPIEvent(new DateTime(2024, 11, 13, 08, 30, 00)),    // CPI

                // December 2024
                NewsEvent.CreateNFPEvent(new DateTime(2024, 12, 06, 08, 30, 00)),    // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2024, 12, 11, 08, 30, 00)),    // CPI
                NewsEvent.CreateFOMCEvent(new DateTime(2024, 12, 18, 19, 00, 00)),   // FOMC

                // January 2025
                new(new DateTime(2025, 01, 08, 13, 15, 0)),
                NewsEvent.CreateNFPEvent(new DateTime(2025, 01, 10, 08, 30, 00)),    // NFP
                new(new DateTime(2025, 01, 14, 13, 30, 0)),
                NewsEvent.CreateCPIEvent(new DateTime(2025, 01, 15, 08, 30, 00)),    // CPI
                new(new DateTime(2025, 01, 16, 13, 30, 0)),
                new(new DateTime(2025, 01, 23, 13, 30, 0)),
                NewsEvent.CreateFOMCEvent(new DateTime(2025, 01, 29, 19, 00, 00)),   // FOMC
                new(new DateTime(2025, 01, 30, 13, 30, 0)),
                new(new DateTime(2025, 01, 31, 13, 30, 0)),

                // February 2025
                new(new DateTime(2025, 02, 05, 13, 15, 0)),
                new(new DateTime(2025, 02, 06, 13, 30, 0)),
                NewsEvent.CreateNFPEvent(new DateTime(2025, 02, 07, 08, 30, 00)),    // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2025, 02, 12, 08, 30, 00)),    // CPI
                new(new DateTime(2025, 02, 13, 13, 30, 0)),
                new(new DateTime(2025, 02, 14, 13, 30, 0)),
                new(new DateTime(2025, 02, 20, 13, 30, 0)),
                new(new DateTime(2025, 02, 27, 13, 30, 0)),
                new(new DateTime(2025, 02, 28, 13, 30, 0)),

                // March 2025
                new(new DateTime(2025, 03, 05, 13, 15, 0)),
                new(new DateTime(2025, 03, 06, 13, 30, 0)),
                NewsEvent.CreateNFPEvent(new DateTime(2025, 03, 07, 08, 30, 00)),    // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2025, 03, 12, 08, 30, 00)),    // CPI
                new(new DateTime(2025, 03, 17, 12, 30, 0)),
                NewsEvent.CreateFOMCEvent(new DateTime(2025, 03, 19, 14, 00, 0)),   // FOMC
                new(new DateTime(2025, 03, 20, 12, 30, 0)),
                new(new DateTime(2025, 03, 27, 12, 30, 0)),
                new(new DateTime(2025, 03, 28, 12, 30, 0)),

                // April 2025
                new(new DateTime(2025, 04, 02, 13, 15, 0)),
                new(new DateTime(2025, 04, 03, 13, 30, 0)),
                NewsEvent.CreateNFPEvent(new DateTime(2025, 04, 04, 08, 30, 00)),    // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2025, 04, 10, 08, 30, 00)),    // CPI
                new(new DateTime(2025, 04, 11, 13, 30, 0)),
                new(new DateTime(2025, 04, 16, 13, 30, 0)),
                new(new DateTime(2025, 04, 17, 13, 30, 0)),
                NewsEvent.CreateAllDay(new DateTime(2025, 04, 18)),                 // EASTER
                NewsEvent.CreateAllDay(new DateTime(2025, 04, 21)),                 // EASTER
                new(new DateTime(2025, 04, 24, 13, 30, 0)),
                new(new DateTime(2025, 04, 30, 13, 30, 0)),

                // May 2025
                new(new DateTime(2025, 05, 01, 13, 30, 0)),
                NewsEvent.CreateNFPEvent(new DateTime(2025, 05, 02, 08, 30, 00)),   // NFP 
                NewsEvent.CreateFOMCEvent(new DateTime(2025, 05, 07, 14, 00, 0)),   // FOMC
                new(new DateTime(2025, 05, 08, 13, 30, 0)),
                NewsEvent.CreateCPIEvent(new DateTime(2025, 05, 13, 08, 30, 00)),    // CPI
                new(new DateTime(2025, 05, 22, 13, 30, 0)),
                new(new DateTime(2025, 05, 29, 13, 30, 0)),
                new(new DateTime(2025, 05, 30, 13, 30, 0)),

                // June 2025
                new(new DateTime(2025, 06, 04, 13, 15, 0)),
                new(new DateTime(2025, 06, 05, 13, 30, 0)),
                NewsEvent.CreateNFPEvent(new DateTime(2025, 06, 06, 08, 30, 00)),    // NFP			
                NewsEvent.CreateCPIEvent(new DateTime(2025, 06, 11, 08, 30, 00)),   // CPI
                new(new DateTime(2025, 06, 12, 13, 30, 0)),
                new(new DateTime(2025, 06, 17, 13, 30, 0)),
                new(new DateTime(2025, 06, 18, 13, 30, 0)),
                NewsEvent.CreateFOMCEvent(new DateTime(2025, 06, 18, 14, 00, 0)),   // FOMC
                new(new DateTime(2025, 06, 26, 13, 30, 0)),
                new(new DateTime(2025, 06, 27, 13, 30, 0)),
                                                                    
                // July 2025                                        
                new(new DateTime(2025, 07, 02, 13, 15, 0)),
                NewsEvent.CreateNFPEvent(new DateTime(2025, 07, 03, 08, 30, 00)),    // NFP
                new(new DateTime(2025, 07, 10, 13, 30, 0)),
                NewsEvent.CreateCPIEvent(new DateTime(2025, 07, 15, 08, 30, 00)),    // CPI
                new(new DateTime(2025, 07, 16, 13, 30, 0)),
                new(new DateTime(2025, 07, 17, 13, 30, 0)),
                new(new DateTime(2025, 07, 24, 13, 30, 0)),
                new(new DateTime(2025, 07, 30, 13, 15, 0)),
                new(new DateTime(2025, 07, 31, 13, 30, 0)),
                NewsEvent.CreateFOMCEvent(new DateTime(2025, 07, 30, 14, 00, 0)),   // FOMC

                // August 2025
                NewsEvent.CreateNFPEvent(new DateTime(2025, 08, 01, 08, 30, 00)),    // NFP
                new(new DateTime(2025, 08, 07, 13, 30, 0)),
                NewsEvent.CreateCPIEvent(new DateTime(2025, 08, 12, 08, 30, 00)),    // CPI
                new(new DateTime(2025, 08, 14, 13, 30, 0)),
                new(new DateTime(2025, 08, 15, 13, 30, 0)),
                new(new DateTime(2025, 08, 21, 13, 30, 0)),
                new(new DateTime(2025, 08, 28, 13, 30, 0)),
                new(new DateTime(2025, 08, 29, 13, 30, 0)),

                // September 2025
                NewsEvent.CreateNFPEvent(new DateTime(2025, 09, 05, 08, 30, 00)),    // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2025, 09, 11, 08, 30, 00)),    // CPI
                NewsEvent.CreateFOMCEvent(new DateTime(2025, 09, 17, 14, 00, 0)),   // FOMC
                                                                                    
                // October 2025
                NewsEvent.CreateCPIEvent(new DateTime(2025, 10, 24, 08, 30, 00)),    // CPI
                NewsEvent.CreateFOMCEvent(new DateTime(2025, 10, 29, 14, 00, 0)),   // FOMC
                                                                                    
                // November 2025
                NewsEvent.CreateNFPEvent(new DateTime(2025, 11, 20, 08, 30, 00)),    // NFP

                // December 2025
                NewsEvent.CreateFOMCEvent(new DateTime(2025, 12, 10, 14, 00, 0)),   // FOMC
                NewsEvent.CreateNFPEvent(new DateTime(2025, 12, 16, 08, 30, 00)),   // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2025, 12, 18, 08, 30, 00)),   // CPI

                // January 2026
                new(new DateTime(2026, 1, 07, 13, 15, 0)),
                new(new DateTime(2026, 1, 08, 13, 30, 0)),
                NewsEvent.CreateNFPEvent(new DateTime(2026, 1, 09, 08, 30, 00)),    // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2026, 1, 13, 08, 30, 00)),    // CPI
                new(new DateTime(2026, 1, 14, 13, 30, 0)),
                new(new DateTime(2026, 1, 15, 13, 30, 0)),
                new(new DateTime(2026, 1, 22, 13, 30, 0)),
                NewsEvent.CreateFOMCEvent(new DateTime(2026, 01, 28, 14, 00, 0)),   // FOMC
                new(new DateTime(2026, 1, 29, 13, 30, 0)),
                new(new DateTime(2026, 1, 30, 13, 30, 0)),

                // February 2026
                new(new DateTime(2026, 2, 04, 13, 15, 0)),
                new(new DateTime(2026, 2, 05, 13, 30, 0)),
                new(new DateTime(2026, 2, 10, 13, 30, 0)),
                NewsEvent.CreateNFPEvent(new DateTime(2026, 2, 11, 08, 30, 00)),    // NFP
                new(new DateTime(2026, 2, 12, 13, 30, 0)),
                NewsEvent.CreateCPIEvent(new DateTime(2026, 2, 13, 08, 30, 00)),    // CPI
                new(new DateTime(2026, 2, 19, 13, 30, 0)),
                new(new DateTime(2026, 2, 20, 13, 30, 0)),
                new(new DateTime(2026, 2, 26, 13, 30, 0)),
                new(new DateTime(2026, 2, 27, 13, 30, 0)),

                // March 2026
                new(new DateTime(2026, 3, 04, 13, 15, 0)),
                new(new DateTime(2026, 3, 05, 13, 30, 0)),
                NewsEvent.CreateNFPEvent(new DateTime(2026, 3, 06, 08, 30, 00)),    // NFP
                new(new DateTime(2026, 3, 10, 12, 30, 0)),
                NewsEvent.CreateCPIEvent(new DateTime(2026, 3, 11, 08, 30, 00)),    // CPI
                new(new DateTime(2026, 3, 12, 12, 30, 0)),
                new(new DateTime(2026, 3, 13, 12, 30, 0)),
                NewsEvent.CreateFOMCEvent(new DateTime(2026, 3, 18, 14, 00, 0)),    // FOMC
                new(new DateTime(2026, 3, 19, 12, 30, 0)),
                new(new DateTime(2026, 3, 23, 11, 06, 0)),
                new(new DateTime(2026, 3, 26, 12, 30, 0)),

                // April 2026
                new(new DateTime(2026, 4, 01, 13, 30, 0)),
                NewsEvent.CreateNFPEvent(new DateTime(2026, 4, 02, 08, 30, 00)),    // NFP
                NewsEvent.CreateAllDay(new DateTime(2026, 4, 03)),                  // Easter Friday and Monday
                NewsEvent.CreateAllDay(new DateTime(2026, 4, 06)),                  // Easter Friday and Monday
                NewsEvent.CreateCPIEvent(new DateTime(2026, 04, 10, 08, 30, 00)),   // CPI
                NewsEvent.CreateFOMCEvent(new DateTime(2026, 04, 29, 14, 00, 0)),   // FOMC

                // May 2026
                NewsEvent.CreateNFPEvent(new DateTime(2026, 05, 08, 08, 30, 00)),   // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2026, 05, 12, 08, 30, 00)),   // CPI
                NewsEvent.CreateUSBankHoliday(new DateTime(2026, 05, 25)),          // US BANK Holiday

                // Jun 2026
                NewsEvent.CreateNFPEvent(new DateTime(2026, 06, 05, 08, 30, 00)),   // NFP
                NewsEvent.CreateCPIEvent(new DateTime(2026, 06, 10, 08, 30, 00)),   // CPI
                NewsEvent.CreateFOMCEvent(new DateTime(2026, 06, 17, 14, 00, 0)),   // FOMC
            };

            // Add xmas period 2024
            for (int i = 23; i <= 31; i++)
            {
                events.Add(NewsEvent.CreateAllDay(new DateTime(2024, 12, i)));
            }

            // Skip start of January 2025
            for (int i = 1; i <= 5; i++)
            {
                events.Add(NewsEvent.CreateAllDay(new DateTime(2026, 01, i)));
            }

            // Add xmas period 2025
            for (int i = 22; i <= 31; i++)
            {
                events.Add(NewsEvent.CreateAllDay(new DateTime(2025, 12, i)));
            }

            // Skip start of January 2026
            for (int i = 1; i <= 4; i++)
            {
                events.Add(NewsEvent.CreateAllDay(new DateTime(2026, 01, i)));
            }

            return events;
        }

        public static List<NewsEvent> AllDay(this List<NewsEvent> events)
        {
            return [.. events.Where(a => a.AllDay)];
        }

        public static List<NewsEvent> BankHolidays(this List<NewsEvent> events)
        {
            return [.. events.Where(a => a.BankHoliday)];
        }

        public static bool IsInWeek(this List<NewsEvent> events, NewsFilter newsFilter, DateTime currentDate)
        {
            var startOfWeek = currentDate.StartOfWeek(DayOfWeek.Monday);
            var endOfWeek = currentDate.EndOfWeek(DayOfWeek.Sunday);
            var filtered = events.Where(e => e.EventTime >= startOfWeek && e.EventTime <= endOfWeek);

            if (newsFilter.IncludeNoEvents && !filtered.Any(e => e.IsNFP || e.IsCPI || e.IsFOMC))
            {
                return true;
            }

            if (newsFilter.IncludeFOMC && filtered.Any(e => e.IsFOMC))
            {
                return true;
            }

            if (newsFilter.IncludeCPI && filtered.Any(e => e.IsCPI))
            {
                return true;
            }

            if (newsFilter.IncludeNFP && filtered.Any(e => e.IsNFP))
            {
                return true;
            }

            return false;
        }

        public static bool IsCPIWeek(this List<NewsEvent> events, DateTime currentDate)
        {
            var startOfWeek = currentDate.StartOfWeek(DayOfWeek.Monday);
            var endOfWeek = currentDate.EndOfWeek(DayOfWeek.Sunday);
            var filtered = events.Any(e => e.IsCPI && e.EventTime >= startOfWeek && e.EventTime <= endOfWeek);
            return filtered;
        }

        public static bool IsNFPWeek(this List<NewsEvent> events, DateTime currentDate)
        {
            var startOfWeek = currentDate.StartOfWeek(DayOfWeek.Monday);
            var endOfWeek = currentDate.EndOfWeek(DayOfWeek.Sunday);
            var filtered = events.Any(e => e.IsNFP && e.EventTime >= startOfWeek && e.EventTime <= endOfWeek);
            return filtered;
        }

        public static bool IsFOMCWeek(this List<NewsEvent> events, DateTime currentDate)
        {
            var startOfWeek = currentDate.StartOfWeek(DayOfWeek.Monday);
            var endOfWeek = currentDate.EndOfWeek(DayOfWeek.Sunday);
            var filtered = events.Any(e => e.IsFOMC && e.EventTime >= startOfWeek && e.EventTime <= endOfWeek);
            return filtered;
        }

        public static bool IsNoEventWeek(this List<NewsEvent> events, DateTime currentDate)
        {
            var startOfWeek = currentDate.StartOfWeek(DayOfWeek.Monday);
            var endOfWeek = currentDate.EndOfWeek(DayOfWeek.Sunday);
            var filtered = events.Where(e => e.EventTime >= startOfWeek && e.EventTime <= endOfWeek);
            return !filtered.Any(e => e.IsNFP || e.IsCPI || e.IsFOMC);
        }
    }
}
