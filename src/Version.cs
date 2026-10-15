using System.Reflection;

// 앱 버전은 여기 한 곳에서만 정의한다. 코드를 수정할 때마다 올리고 CHANGELOG.md 에 기록한다.
[assembly: AssemblyVersion(TaskbarGroup.AppVersion.Number + ".0")]
[assembly: AssemblyFileVersion(TaskbarGroup.AppVersion.Number + ".0")]
[assembly: AssemblyInformationalVersion(TaskbarGroup.AppVersion.Number)]

namespace TaskbarGroup
{
    public static class AppVersion
    {
        public const string Number = "2.0.16";

        public static string Display { get { return "v" + Number; } }
    }
}
