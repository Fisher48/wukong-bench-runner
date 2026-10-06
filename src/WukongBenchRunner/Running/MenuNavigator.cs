namespace WukongBenchRunner.Running;

public sealed record MenuLayout(double StartButtonX, double StartButtonY, double ConfirmButtonX, double ConfirmButtonY)
{
    public static MenuLayout Default { get; } = new(
        StartButtonX: 0.115,
        StartButtonY: 0.452,
        ConfirmButtonX: 0.393,
        ConfirmButtonY: 0.580);
}

public enum MenuStep
{
    SkipIntro,
    OpenBenchmark,
    Confirm,
}

public sealed class MenuNavigator(IInputBackend input, MenuLayout layout)
{
    public string InputName => input.Name;

    public MenuStep StepFor(int iteration) => (MenuStep)(iteration % 3);

    public bool Perform(MenuStep step, WindowInfo window)
    {
        input.Activate(window);
        Thread.Sleep(400);

        return step switch
        {
            MenuStep.SkipIntro => input.PressReturn(),
            MenuStep.OpenBenchmark => input.ClickRelative(window, layout.StartButtonX, layout.StartButtonY),
            MenuStep.Confirm => input.ClickRelative(window, layout.ConfirmButtonX, layout.ConfirmButtonY),
            _ => false,
        };
    }
}
