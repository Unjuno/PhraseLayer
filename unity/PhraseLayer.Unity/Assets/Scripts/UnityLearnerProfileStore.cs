using System;
using PhraseLayer.Core.Learning;

#if UNITY_5_3_OR_NEWER
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
#endif

namespace PhraseLayer.Unity
{
#if UNITY_5_3_OR_NEWER
    /// <summary>
    /// Small local learner-profile store under Application.persistentDataPath.
    ///
    /// Save protocol:
    /// 1. write a complete temp file;
    /// 2. move the previous primary to .bak;
    /// 3. move temp to primary;
    /// 4. remove backup only after the new primary exists.
    ///
    /// If the process stops between steps 2 and 3, Load() recovers from the backup.
    /// Core remains unaware of JSON and filesystem APIs.
    /// </summary>
    public sealed class UnityLearnerProfileStore : ILearnerProfileStore
    {
        public const string DefaultFileName = "learner-profile-v1.json";
        private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

        public UnityLearnerProfileStore(string filePath = null)
        {
            var resolved = string.IsNullOrWhiteSpace(filePath)
                ? Path.Combine(Application.persistentDataPath, "PhraseLayer", DefaultFileName)
                : filePath;
            FilePath = Path.GetFullPath(resolved);
        }

        public string FilePath { get; }
        public string BackupPath => FilePath + ".bak";
        public string TemporaryPath => FilePath + ".tmp";

        public LearnerProfileSnapshot? Load()
        {
            if (File.Exists(FilePath))
                return ReadSnapshot(FilePath);
            if (File.Exists(BackupPath))
                return ReadSnapshot(BackupPath);
            return null;
        }

        public void Save(LearnerProfileSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var directory = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("Learner profile path must have a parent directory.");
            Directory.CreateDirectory(directory);

            var dto = ProfileDto.FromSnapshot(snapshot);
            var json = JsonUtility.ToJson(dto, prettyPrint: true);
            File.WriteAllText(TemporaryPath, json, Utf8WithoutBom);

            if (File.Exists(BackupPath))
                File.Delete(BackupPath);
            if (File.Exists(FilePath))
                File.Move(FilePath, BackupPath);

            try
            {
                File.Move(TemporaryPath, FilePath);
            }
            catch
            {
                if (!File.Exists(FilePath) && File.Exists(BackupPath))
                    File.Move(BackupPath, FilePath);
                throw;
            }

            if (File.Exists(BackupPath))
                File.Delete(BackupPath);
        }

        private static LearnerProfileSnapshot ReadSnapshot(string path)
        {
            var json = File.ReadAllText(path, Utf8WithoutBom);
            var dto = JsonUtility.FromJson<ProfileDto>(json);
            if (dto == null)
                throw new InvalidDataException("Learner profile JSON did not deserialize to an object: " + path);
            return dto.ToSnapshot();
        }

        [Serializable]
        private sealed class ProfileDto
        {
            public int schemaVersion = LearnerProfileSnapshot.CurrentSchemaVersion;
            public double defaultUnderstanding = 0.55;
            public List<EntryDto> entries = new List<EntryDto>();

            public static ProfileDto FromSnapshot(LearnerProfileSnapshot snapshot)
            {
                var dto = new ProfileDto
                {
                    schemaVersion = snapshot.SchemaVersion,
                    defaultUnderstanding = snapshot.DefaultUnderstanding,
                    entries = new List<EntryDto>(snapshot.Entries.Count),
                };
                foreach (var entry in snapshot.Entries)
                    dto.entries.Add(new EntryDto { text = entry.Text, understanding = entry.Understanding });
                return dto;
            }

            public LearnerProfileSnapshot ToSnapshot()
            {
                if (entries == null)
                    throw new InvalidDataException("Learner profile entries array is missing.");

                var converted = new List<LearnerKnowledgeEntry>(entries.Count);
                foreach (var entry in entries)
                {
                    if (entry == null)
                        throw new InvalidDataException("Learner profile contains a null entry.");
                    converted.Add(new LearnerKnowledgeEntry(entry.text, entry.understanding));
                }
                return new LearnerProfileSnapshot(schemaVersion, defaultUnderstanding, converted);
            }
        }

        [Serializable]
        private sealed class EntryDto
        {
            public string text = string.Empty;
            public double understanding;
        }
    }
#else
    /// <summary>
    /// Host-CI fallback; real persistence is available only inside Unity where persistentDataPath exists.
    /// </summary>
    public sealed class UnityLearnerProfileStore : ILearnerProfileStore
    {
        public LearnerProfileSnapshot? Load()
        {
            throw new NotSupportedException("UnityLearnerProfileStore requires a Unity runtime.");
        }

        public void Save(LearnerProfileSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            throw new NotSupportedException("UnityLearnerProfileStore requires a Unity runtime.");
        }
    }
#endif
}
