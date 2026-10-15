using OpenOutlook.Desktop;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

public sealed class ContactFormatTests
{
    [Theory]
    [InlineData("Ann Example", "", "Ann", "", "Example", "")]
    [InlineData("Dr. Ann B. Example Jr.", "Dr.", "Ann", "B.", "Example", "Jr.")]
    [InlineData("Example, Ann B.", "", "Ann", "B.", "Example", "")]
    [InlineData("Coworkers", "", "Coworkers", "", "", "")]
    [InlineData("  Mary   Ann   de la Cruz ", "", "Mary", "Ann de la", "Cruz", "")]
    public void Full_names_split_like_outlook(string full, string title, string given, string middle, string surname, string suffix)
    {
        var p = ContactFormat.ParseFullName(full);
        Assert.Equal((title, given, middle, surname, suffix), (p.Title, p.Given, p.Middle, p.Surname, p.Suffix));
    }

    [Fact]
    public void File_as_choices_and_sort_keys_follow_outlook()
    {
        Assert.Equal(["Bryan, Michael", "Michael Bryan", "Intel", "Bryan, Michael (Intel)", "Intel (Bryan, Michael)"],
            ContactFormat.FileAsChoices("Michael", "Bryan", "Intel"));
        Assert.Equal(["Coworkers"], ContactFormat.FileAsChoices("Coworkers", "", ""));
        Assert.Equal("Bryan, Michael", ContactFormat.SortKey(new GraphContact { DisplayName = "Michael Bryan", GivenName = "Michael", Surname = "Bryan" }));
        Assert.Equal("Home, Gig Harbor", ContactFormat.SortKey(new GraphContact { FileAs = "Home, Gig Harbor", DisplayName = "Gig Harbor Home" }));
        Assert.Equal("b", ContactFormat.IndexKey("Bryan, Michael"));
        Assert.Equal("e", ContactFormat.IndexKey("Émile"));
        Assert.Equal("123", ContactFormat.IndexKey("123 Movers"));
        Assert.Equal("123", ContactFormat.IndexKey("아시아나"));
    }

    [Fact]
    public void Initials_and_avatar_colours_are_stable()
    {
        Assert.Equal("AE", ContactFormat.Initials("Ann Example"));
        Assert.Equal("C", ContactFormat.Initials("Coworkers"));
        Assert.Equal("GH", ContactFormat.Initials("Gig Harbor Home"));
        Assert.Equal("?", ContactFormat.Initials("  "));
        Assert.Equal(ContactFormat.AvatarColor("Ann Example"), ContactFormat.AvatarColor("ann example"));
    }

    [Theory]
    [InlineData("1 Main St\nSeattle, WA 98101\nUSA", "1 Main St", "Seattle", "WA", "98101", "USA")]
    [InlineData("1 Main St\nSuite 200\nSeattle WA 98101", "1 Main St\nSuite 200", "Seattle", "WA", "98101", "")]
    [InlineData("10 Downing St\nLondon, SW1A 2AA\nUnited Kingdom", "10 Downing St", "London", "", "SW1A 2AA", "United Kingdom")]
    [InlineData("PO Box 111588", "PO Box 111588", "", "", "", "")]
    public void Address_box_round_trips(string text, string street, string city, string state, string postal, string country)
    {
        var a = ContactFormat.ParseAddress(text);
        Assert.Equal((street, city, state, postal, country), (a.Street, a.City, a.State, a.PostalCode, a.CountryOrRegion));
        Assert.Equal(a, ContactFormat.ParseAddress(ContactFormat.FormatAddress(a)));
    }

    [Fact]
    public void Shown_name_and_sub_line_fall_back_sensibly()
    {
        Assert.Equal("Contoso", ContactFormat.ShownName(new GraphContact { CompanyName = "Contoso" }));
        Assert.Equal("a@example.test", ContactFormat.ShownName(new GraphContact { Emails = [new("a@example.test", "")] }));
        Assert.Equal("1 253-313-5728", ContactFormat.SubLine(new GraphContact { HomePhones = ["1 253-313-5728"] }));
    }
}
