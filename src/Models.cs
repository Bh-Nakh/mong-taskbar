using System;
using System.Collections.Generic;

namespace TaskbarGroup
{
    public class Entry
    {
        public string Name { get; set; }
        public string Target { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public string IconLocation { get; set; }

        public Entry()
        {
            Name = "";
            Target = "";
            Arguments = "";
            WorkingDirectory = "";
            IconLocation = "";
        }
    }

    public class Group
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string IconLocation { get; set; }
        public List<Entry> Entries { get; set; }

        public Group()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "";
            IconLocation = "";
            Entries = new List<Entry>();
        }

        public override string ToString() { return Name; }
    }

    public class Config
    {
        public List<Group> Groups { get; set; }

        /// <summary>바로가기(.lnk)를 만들 폴더. 비어 있으면 실행 파일 폴더의 "바로가기".
        /// 실행 파일 폴더 아래라면 상대 경로로 저장된다.</summary>
        public string ShortcutFolder { get; set; }

        /// <summary>Windows 시작 시 백그라운드 도우미(드래그 앤 드롭·자동 정렬) 실행.</summary>
        public bool AutoStartAgent { get; set; }

        /// <summary>그룹 안의 앱이 실행되면 작업표시줄 버튼을 그룹 아이콘 오른쪽으로 정렬.</summary>
        public bool ArrangeRunningApps { get; set; }

        /// <summary>정렬 방식. "NextToGroup": 각 그룹 아이콘 바로 오른쪽 (기본)
        /// "AfterLastGroup": 그룹 아이콘들은 붙여 두고, 맨 오른쪽 그룹 아이콘 뒤에 그룹 순서·그룹 내 순서대로 모아서.</summary>
        public string ArrangeMode { get; set; }

        /// <summary>"아이콘·정보 다시 읽기" 를 누를 때마다 1씩 오른다 — 그룹 아이콘 파일 이름이 바뀌어
        /// Windows 아이콘 캐시를 거치지 않고 새로 읽히게 하고, 도우미는 이 값이 바뀌면 기록을 비운다.</summary>
        public int IconRevision { get; set; }

        /// <summary>그룹 앱 버튼이 만들어지는 순간 바로 제자리에 붙게 한다 (explorer 에 hook\placer.dll 적용).
        /// 꺼 두면 뜬 뒤에 도우미가 옮긴다.</summary>
        public bool InstantPlacement { get; set; }

        public const string ModeNextToGroup = "NextToGroup";
        public const string ModeAfterLastGroup = "AfterLastGroup";

        public Config()
        {
            Groups = new List<Group>();
            ShortcutFolder = "";
            AutoStartAgent = true;
            ArrangeRunningApps = true;
            ArrangeMode = ModeNextToGroup;
            InstantPlacement = true;
        }
    }
}
