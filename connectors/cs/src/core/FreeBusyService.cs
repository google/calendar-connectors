/* Copyright (c) 2008 Google Inc. All Rights Reserved
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.Configuration;
using System.DirectoryServices;
using System.Net;
using System.Text;

using Google.GCalExchangeSync.Library.Util;
using Google.GCalExchangeSync.Library.WebDav;

namespace Google.GCalExchangeSync.Library
{
    /// <summary>
    /// This class handles read / write requests for Exchange Free / Busy and Appointment data.
    /// </summary>
    public class FreeBusyService
    {
        private static readonly log4net.ILog log =
           log4net.LogManager.GetLogger(typeof(FreeBusyService));

        private static HttpHeader[] kBriefHeader = { new HttpHeader("Brief", "t") };

        private string exchangeServerUrl;
        private WebDavQuery webDavQuery;
        private WebDavQueryBuilder webDavQueryBuilder;

        /// <summary>
        /// Constructor for an Exchange Gateway
        /// </summary>
        /// <param name="exchangeServer">Exchange server address for exchange searches</param>
        /// <param name="webdav">WebDAV query service</param>
        /// <param name="queryBuilder">Optional WebDAV query builder service</param>
        public FreeBusyService(
            string exchangeServer,
            WebDavQuery webdav,
            WebDavQueryBuilder queryBuilder)
        {
            exchangeServerUrl = exchangeServer;
            webDavQuery = webdav;
            webDavQueryBuilder = queryBuilder ?? new WebDavQueryBuilder();
        }

        /// <summary>
        /// Returns the free busy times for the specified exchange users
        /// </summary>
        /// <param name="users">The user which free/busy blocks will be looked up for</param>
        /// <param name="window">The time period to look up FB info for</param>
        /// <returns></returns>
        public Dictionary<ExchangeUser, FreeBusy> LookupFreeBusyTimes(
            ExchangeUserDict users,
            DateTimeRange window )
        {
            /* Create an array of mailboxes to retrieve from exchange */
            Dictionary<ExchangeUser, FreeBusy> result = new Dictionary<ExchangeUser, FreeBusy>();

            try
            {
                using (BlockTimer bt = new BlockTimer("LookupFreeBusyTimes"))
                {
                    /* Perform the retrieval of free busy times through WebDav */
                    result = webDavQuery.LoadFreeBusy(exchangeServerUrl, users, window);
                }
            }
            catch (Exception ex)
            {
                throw new GCalExchangeException(
                    GCalExchangeErrorCode.ExchangeUnreachable,
                   "Error occured while retrieving free busy ranges",
                   ex);
            }

            return result;
        }

        /// <summary>
        /// Returns the free busy times for the specified exchange users
        /// </summary>
        /// <param name="user">The user which free/busy blocks will be looked up for</param>
        /// <param name="window">The date range to do the lookup</param>
        /// <returns>FreeBusy data for user in the daterange</returns>
        public FreeBusy LookupFreeBusyTimes( ExchangeUser user, DateTimeRange window )
        {
            ExchangeUserDict users = new ExchangeUserDict();
            users.Add( user.Email, user );

            Dictionary<ExchangeUser, FreeBusy> result = LookupFreeBusyTimes ( users, window );

            return result[user];
        }

        private void BuildSpecializeFreeBusyMessageQuery(string targetUsername)
        {
            // TODO: BUG: fix "conversation" message attribute
            webDavQueryBuilder.AddUpdateProperty(MessageProperty.Subject, targetUsername);
            webDavQueryBuilder.AddUpdateProperty(MessageProperty.NormalizedSubject, targetUsername);
            webDavQueryBuilder.AddUpdateProperty(MessageProperty.ConversationTopic, targetUsername);
            webDavQueryBuilder.AddUpdateProperty(MessageProperty.SubjectPrefix, string.Empty);
        }

        private void BuildSetFreeBusyQuery(
            List<string> busyMonths,
            List<string> busyDailyData,
            List<string> tentativeMonths,
            List<string> tentativeDailyData,
            string startDate,
            string endDate,
            bool clearOOF)
        {
            if (busyMonths.Count != busyDailyData.Count)
            {
                log.WarnFormat("Mismatch between the busy months and daily data: {0} {1}",
                               busyMonths.Count,
                               busyDailyData.Count);
            }

            if (tentativeMonths.Count != tentativeDailyData.Count)
            {
                log.WarnFormat("Mismatch between the tentative months and daily data: {0} {1}",
                               tentativeMonths.Count,
                               tentativeDailyData.Count);
            }

            if (busyMonths.Count == 0)
            {
                webDavQueryBuilder.AddRemoveProperty(FreeBusyProperty.BusyMonths);
                webDavQueryBuilder.AddRemoveProperty(FreeBusyProperty.MergedMonths);
            }
            else
            {
                webDavQueryBuilder.AddUpdateProperty(FreeBusyProperty.BusyMonths,
                                                     busyMonths);
                // Merged should be the union of busy and OOF, but since we don't have OOF,
                // it is exact copy of busy
                webDavQueryBuilder.AddUpdateProperty(FreeBusyProperty.MergedMonths,
                                                     busyMonths);
            }

            if (busyDailyData.Count == 0)
            {
                webDavQueryBuilder.AddRemoveProperty(FreeBusyProperty.BusyEvents);
                webDavQueryBuilder.AddRemoveProperty(FreeBusyProperty.MergedEvents);
            }
            else
            {
                webDavQueryBuilder.AddUpdateProperty(FreeBusyProperty.BusyEvents,
                                                     busyDailyData);
                // Merged should be the union of busy and OOF, but since we don't have OOF,
                // it is exact copy of busy
                webDavQueryBuilder.AddUpdateProperty(FreeBusyProperty.MergedEvents,
                                                     busyDailyData);
            }

            if (tentativeMonths.Count == 0)
            {
                webDavQueryBuilder.AddRemoveProperty(FreeBusyProperty.TentativeMonths);
            }
            else
            {
                webDavQueryBuilder.AddUpdateProperty(FreeBusyProperty.TentativeMonths,
                                                     tentativeMonths);
            }

            if (tentativeDailyData.Count == 0)
            {
                webDavQueryBuilder.AddRemoveProperty(FreeBusyProperty.TentativeEvents);
            }
            else
            {
                webDavQueryBuilder.AddUpdateProperty(FreeBusyProperty.TentativeEvents,
                                                     tentativeDailyData);
            }

            if (clearOOF)
            {
                webDavQueryBuilder.AddRemoveProperty(FreeBusyProperty.OutOfOfficeEvents);
                webDavQueryBuilder.AddRemoveProperty(FreeBusyProperty.OutOfOfficeMonths);
            }

            webDavQueryBuilder.AddUpdateProperty(FreeBusyProperty.StartOfPublishedRange,
                                                 startDate);
            webDavQueryBuilder.AddUpdateProperty(FreeBusyProperty.EndOfPublishedRange,
                                                 endDate);
        }

        private void BuildSpecialFreeBusyPropertiesQuery()
        {
            webDavQueryBuilder.AddUpdateProperty(FreeBusyProperty.ScheduleInfoResourceType,
                                                 "0");
            webDavQueryBuilder.AddUpdateProperty(FreeBusyProperty.DisableFullFidelity,
                                                 "1");
            // Technically those two (LocaleId and MessageLocaleId) should not be neeed,
            // but if not explicitly set they are 0, which potentially could cause problems
            // for readers.
            webDavQueryBuilder.AddUpdateProperty(FreeBusyProperty.MessageLocaleId,
                                                 "1033");
            webDavQueryBuilder.AddUpdateProperty(FreeBusyProperty.LocaleId,
                                                 "1033");
            webDavQueryBuilder.AddUpdateProperty(FreeBusyProperty.FreeBusyRangeTimestamp,
                                                 DateUtil.FormatDateForExchange(DateUtil.NowUtc));
        }

        /// <summary>
        /// Create a FreeBusy message on the server for the given URL filling the given data
        /// </summary>
        /// <param name="targetUrl">Destination message URL to create</param>
        /// <param name="targetUsername">The user to create the FB for</param>
        /// <param name="busyMonths">The busy months in Exchange format</param>
        /// <param name="busyDailyData">The busy times in Exchange format</param>
        /// <param name="tentativeMonths">The tentative months in Exchange format</param>
        /// <param name="tentativeDailyData">The tentative times in Exchange format</param>
        /// <param name="startDate"></param>
        /// <param name="endDate"></param>
        public void CreateFreeBusyMessage(
            string targetUrl,
            string targetUsername,
            List<string> busyMonths,
            List<string> busyDailyData,
            List<string> tentativeMonths,
            List<string> tentativeDailyData,
            string startDate,
            string endDate)

        {
            if (log.IsDebugEnabled)
            {
                log.DebugFormat("Creating free/busy message for: {0}", targetUrl);
            }

            try
            {
                webDavQueryBuilder.Reset();

                BuildSpecialFreeBusyPropertiesQuery();
                BuildSpecializeFreeBusyMessageQuery(targetUsername);
                BuildSetFreeBusyQuery(busyMonths,
                                      busyDailyData,
                                      tentativeMonths,
                                      tentativeDailyData,
                                      startDate,
                                      endDate,
                                      true);

                webDavQuery.IssueRequestIgnoreResponse(targetUrl,
                                                       Method.PROPPATCH,
                                                       webDavQueryBuilder.BuildQueryBody(),
                                                       kBriefHeader);
                if (log.IsDebugEnabled)
                {
                    log.DebugFormat("Creating free/busy message for: {0} succeeded.",
                                    targetUrl);
                }
            }
            catch (Exception ex)
            {
                log.Error(string.Format("Creating free/busy message for: {0} failed",
                                        targetUrl),
                          ex);

                throw;
            }
        }
    }
}
