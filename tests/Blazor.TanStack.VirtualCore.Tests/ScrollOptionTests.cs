using Shouldly;
using Xunit;

namespace Kebechet.Blazor.TanStack.VirtualCore.Tests;

public class ScrollOptionTests
{
	[Fact]
	public void ToJsName_EachAlignment_MatchesTheVirtualizerOptionValue()
	{
		// Arrange & Act & Assert
		// These are @tanstack/virtual-core's own accepted values for scrollToIndex({ align }).
		// An unrecognised string is silently ignored by the virtualizer, so a typo here produces a
		// scroll that lands in the wrong place rather than an error.
		ScrollAlignment.Auto.ToJsName().ShouldBe("auto");
		ScrollAlignment.Start.ToJsName().ShouldBe("start");
		ScrollAlignment.Center.ToJsName().ShouldBe("center");
		ScrollAlignment.End.ToJsName().ShouldBe("end");
	}

	[Fact]
	public void ToJsName_EachBehavior_MatchesTheVirtualizerOptionValue()
	{
		// Arrange & Act & Assert
		ScrollBehavior.Auto.ToJsName().ShouldBe("auto");
		ScrollBehavior.Smooth.ToJsName().ShouldBe("smooth");
		ScrollBehavior.Instant.ToJsName().ShouldBe("instant");
	}

	[Fact]
	public void ToJsName_EveryDeclaredValue_IsMapped()
	{
		// Arrange
		var alignments = Enum.GetValues<ScrollAlignment>();
		var behaviors = Enum.GetValues<ScrollBehavior>();

		// Act & Assert
		foreach (var alignment in alignments)
		{
			Should.NotThrow(() => alignment.ToJsName());
		}

		foreach (var behavior in behaviors)
		{
			Should.NotThrow(() => behavior.ToJsName());
		}
	}
}
