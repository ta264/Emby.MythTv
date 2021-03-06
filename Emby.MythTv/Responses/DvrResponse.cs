﻿using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.MythTv.Helpers;
using Emby.MythTv.Model;

namespace Emby.MythTv.Responses
{
    public class ExistingTimerException : Exception
    {
        public string id { get; private set; }

        public ExistingTimerException(string id)
            : base($"Existing timer {id}")
        {
            this.id = id;
        }
    }

    public class DvrResponse
    {
        private Dictionary<string, StorageGroupMap> StorageGroups;

        public DvrResponse() {}
        
        public DvrResponse(List<StorageGroupMap> groups)
        {
            StorageGroups = groups.ToDictionary(x => x.GroupName);
        }

        public List<string> GetRecGroupList(Stream stream, IJsonSerializer json, ILogger logger)
        {
            var root = json.DeserializeFromStream<RootRecGroupList>(stream);
            var ans = root.StringList;
            ans.Add("Deleted");
            return ans;
        }

        private class RootRecGroupList
        {
            public List<string> StringList { get; set; }
        }

        public IEnumerable<SeriesTimerInfo> GetSeriesTimers(Stream stream, IJsonSerializer json, ILogger logger)
        {

            var root = json.DeserializeFromStream<RecRuleListRoot>(stream);
            return root.RecRuleList.RecRules
                .Where(rule => rule.Type.Equals("Record All"))
                .Select(i => RecRuleToSeriesTimerInfo(i));

        }

        private class RecRuleListRoot
        {
            public RecRuleList RecRuleList { get; set; }
        }

        private SeriesTimerInfo RecRuleToSeriesTimerInfo(RecRule item)
        {
            var info = new SeriesTimerInfo()
            {
                Name = item.Title,
                ChannelId = item.ChanId,
                EndDate = item.EndTime,
                StartDate = item.StartTime,
                Id = item.Id,
                PrePaddingSeconds = item.StartOffset * 60,
                PostPaddingSeconds = item.EndOffset * 60,
                RecordAnyChannel = !((item.Filter & RecFilter.ThisChannel) == RecFilter.ThisChannel),
                RecordAnyTime = !((item.Filter & RecFilter.ThisDayTime) == RecFilter.ThisDayTime),
                RecordNewOnly = ((item.Filter & RecFilter.NewEpisode) == RecFilter.NewEpisode),
                ProgramId = item.ProgramId,
                SeriesId = item.SeriesId,
                KeepUpTo = item.MaxEpisodes
            };

            return info;

        }

        private RecRule GetOneRecRule(Stream stream, IJsonSerializer json, ILogger logger)
        {
            var root = json.DeserializeFromStream<RecRuleRoot>(stream);
            logger.Debug(string.Format("[MythTV] GetOneRecRule Response: {0}",
                                       json.SerializeToString(root)));
            return root.RecRule;
        }

        private class RecRuleRoot
        {
            public RecRule RecRule { get; set; }
        }

        public SeriesTimerInfo GetDefaultTimerInfo(Stream stream, IJsonSerializer json, ILogger logger)
        {
            return RecRuleToSeriesTimerInfo(GetOneRecRule(stream, json, logger));
        }

        public string GetNewSeriesTimerJson(SeriesTimerInfo info, Stream stream, IJsonSerializer json, ILogger logger)
        {

            RecRule orgRule = GetOneRecRule(stream, json, logger);
            if (orgRule != null)
            {
                orgRule.Type = "Record All";

                if (info.RecordAnyChannel)
                    orgRule.Filter &= ~RecFilter.ThisChannel;
                else
                    orgRule.Filter |= RecFilter.ThisChannel;
                if (info.RecordAnyTime)
                    orgRule.Filter &= ~RecFilter.ThisDayTime;
                else
                    orgRule.Filter |= RecFilter.ThisDayTime;
                if (info.RecordNewOnly)
                    orgRule.Filter |= RecFilter.NewEpisode;
                else
                    orgRule.Filter &= ~RecFilter.NewEpisode;

                orgRule.MaxEpisodes = info.KeepUpTo;
                orgRule.MaxNewest = info.KeepUpTo > 0;
                orgRule.StartOffset = info.PrePaddingSeconds / 60;
                orgRule.EndOffset = info.PostPaddingSeconds / 60;

            }

            var output = json.SerializeToString(orgRule);
            logger.Info($"[MythTV RuleResponse: generated new timer json:\n{output}");

            return output;
        }

        public string GetNewTimerJson(TimerInfo info, Stream stream, IJsonSerializer json, ILogger logger)
        {

            RecRule rule = GetOneRecRule(stream, json, logger);

            // check if there is an existing rule that is going to cause grief
            if (rule.Type != "Not Recording")
                throw new ExistingTimerException(rule.Id);

            rule.Type = "Single Record";
            rule.StartOffset = info.PrePaddingSeconds / 60;
            rule.EndOffset = info.PostPaddingSeconds / 60;

            var output = json.SerializeToString(rule);
            logger.Info($"[MythTV RuleResponse: generated new timer json:\n{output}");

            return output;
        }

        public string GetNewDoNotRecordTimerJson(Stream stream, IJsonSerializer json, ILogger logger)
        {

            RecRule rule = GetOneRecRule(stream, json, logger);
            rule.Type = "Do not Record";

            var output = json.SerializeToString(rule);
            logger.Info($"[MythTV RuleResponse: generated new timer json:\n{output}");

            return output;
        }

        public List<TimerInfo> GetUpcomingList(Stream stream, IJsonSerializer json, ILogger logger)
        {

            var root = json.DeserializeFromStream<ProgramListRoot>(stream);
            return root.ProgramList.Programs.Select(i => ProgramToTimerInfo(i)).ToList();

        }

        private class ProgramListRoot
        {
            public ProgramList ProgramList { get; set; }
        }

        private TimerInfo ProgramToTimerInfo(Program item)
        {

            string id = $"{item.Channel.ChanId}_{((DateTime)item.StartTime).Ticks}";

            TimerInfo timer = new TimerInfo()
            {
                ChannelId = item.Channel.ChanId,
                ProgramId = id,
                Name = item.Title,
                Overview = item.Description,
                StartDate = (DateTime)item.StartTime,
                EndDate = (DateTime)item.EndTime,
                Status = RecordingStatus.New,
                SeasonNumber = item.Season,
                EpisodeNumber = item.Episode,
                EpisodeTitle = item.Title,
                IsRepeat = item.Repeat
            };


            // see https://code.mythtv.org/doxygen/recordingtypes_8h_source.html#l00022
            if (item.Recording.RecType == 4)
            {
                // Only add on SeriesTimerId if a "Record All" rule
                timer.SeriesTimerId = item.Recording.RecordId;

                // Also set a unique id for this instance
                timer.Id = id;
            }
            else
            {
                // Use the mythtv rule ID for single recordings
                timer.Id = item.Recording.RecordId;
            }

            timer.PrePaddingSeconds = (int)(timer.StartDate - item.Recording.StartTs).TotalSeconds;
            timer.PostPaddingSeconds = (int)(item.Recording.EndTs - timer.EndDate).TotalSeconds;

            timer.IsPrePaddingRequired = timer.PrePaddingSeconds > 0;
            timer.IsPostPaddingRequired = timer.PostPaddingSeconds > 0;

            return timer;
        }

        public IEnumerable<RecordingInfo> GetRecordings(Stream stream, IJsonSerializer json, ILogger logger, IFileSystem fileSystem)
        {

            var included = Plugin.Instance.Configuration.RecGroups.Where(x => x.Enabled == true).Select(x => x.Name).ToList();
            var root = json.DeserializeFromStream<ProgramListRoot>(stream);
            return root.ProgramList.Programs
                .Where(i => included.Contains(i.Recording.RecGroup))
                .Where(i => i.FileSize > 0)
                .Select(i => ProgramToRecordingInfo(i, fileSystem));

        }

        private RecordingStatus RecStatusToRecordingStatus(RecStatus item)
        {
            switch (item)
            {
                case RecStatus.Recorded:
                    return RecordingStatus.Completed;
                case RecStatus.Recording:
                    return RecordingStatus.InProgress;
                case RecStatus.Cancelled:
                    return RecordingStatus.Cancelled;
            }

            return RecordingStatus.Error;
        }

        private RecordingInfo ProgramToRecordingInfo(Program item, IFileSystem fileSystem)
        {

            RecordingInfo recInfo = new RecordingInfo()
            {
                Id = item.Recording.RecordedId,
                SeriesTimerId = item.Recording.RecordId,
                ChannelId = item.Channel.ChanId,
                ChannelType = ChannelType.TV,
                Name = item.Title,
                Overview = item.Description,
                StartDate = item.StartTime,
                EndDate = item.EndTime,
                ProgramId = $"{item.Channel.ChanId}_{item.StartTime.Ticks}",
                Status = RecStatusToRecordingStatus(item.Recording.Status),
                IsRepeat = item.Repeat,
                IsHD = (item.VideoProps & VideoFlags.VID_HDTV) == VideoFlags.VID_HDTV,
                Audio = ProgramAudio.Stereo,
                OriginalAirDate = item.Airdate,
                IsMovie = item.CatType == "movie",
                IsSports = item.CatType == "sports" ||
                    GeneralHelpers.ContainsWord(item.Category, "sport",
                                                StringComparison.OrdinalIgnoreCase) ||
                    GeneralHelpers.ContainsWord(item.Category, "motor sports",
                                                StringComparison.OrdinalIgnoreCase) ||
                    GeneralHelpers.ContainsWord(item.Category, "football",
                                                StringComparison.OrdinalIgnoreCase) ||
                    GeneralHelpers.ContainsWord(item.Category, "cricket",
                                                StringComparison.OrdinalIgnoreCase),
                IsSeries = item.CatType == "series" || item.CatType == "tvshow",
                IsNews = GeneralHelpers.ContainsWord(item.Category, "news",
                                                         StringComparison.OrdinalIgnoreCase),
                IsKids = GeneralHelpers.ContainsWord(item.Category, "animation",
                                                         StringComparison.OrdinalIgnoreCase),
                ShowId = item.ProgramId,

            };
            if (!string.IsNullOrEmpty(item.SubTitle)) {
                recInfo.EpisodeTitle = item.SubTitle;
            } else if (item.Season != null && item.Episode != null && item.Season > 0 && item.Episode > 0) {
                recInfo.EpisodeTitle = string.Format("{0:D}x{1:D2}", item.Season, item.Episode);
            } else {
                recInfo.EpisodeTitle = item.Airdate.ToString("yyyy-MM-dd");
            }

            string recPath = Path.Combine(StorageGroups[item.Recording.StorageGroup].DirNameEmby, item.FileName);
            if (fileSystem.FileExists(recPath))
            {
                recInfo.Path = recPath;
            }
            else
            {
                recInfo.Url = string.Format("{0}/Content/GetFile?StorageGroup={1}&FileName={2}",
                                            Plugin.Instance.Configuration.WebServiceUrl,
                                            item.Recording.StorageGroup,
                                            item.FileName);
            }

            recInfo.Genres.AddRange(item.Category.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries));

            recInfo.HasImage = false;
            if (item.Artwork.ArtworkInfos.Count > 0)
            {
                var art = item.Artwork.ArtworkInfos.Where(i => i.Type.Equals("coverart"));
                if (art.Any())
                {
                    var url = item.Artwork.ArtworkInfos.Where(i => i.Type.Equals("coverart")).First().URL;
                    recInfo.ImageUrl = string.Format("{0}{1}",
                                                     Plugin.Instance.Configuration.WebServiceUrl,
                                                     url);
                    recInfo.HasImage = true;
                }
            }

            return recInfo;

        }
    }
}
