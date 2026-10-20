namespace NzbDrone.Core.CustomFormats
{
    public class ReleaseTitleSpecification : RegexSpecificationBase
    {
        public override int Order => 1;
        public override string ImplementationName => "Release Title";
        public override string InfoLink => "https://github.com/DanCue44/Talearr/issues";

        protected override bool IsSatisfiedByWithoutNegate(CustomFormatInput input)
        {
            return MatchString(input.BookInfo?.ReleaseTitle) || MatchString(input.Filename);
        }
    }
}
