using Avalonia.Controls;
using DeskShare.DesktopAvalonia.Localization;
using MsBox.Avalonia.Enums;

namespace DeskShare.DesktopAvalonia.Dialogs;

/// <summary>Localized notifications and confirmations using the same accessible dialog as sharing.</summary>
public sealed class LocalizedMessageBox
{
    private readonly string _title;
    private readonly string _message;
    private readonly ButtonEnum _buttons;

    private LocalizedMessageBox(string title, string message, ButtonEnum buttons)
    {
        _title = title;
        _message = message;
        _buttons = buttons;
    }

    public static LocalizedMessageBox Create(string title, string message, ButtonEnum buttons, Icon icon)
    {
        if (buttons is not (ButtonEnum.Ok or ButtonEnum.YesNo)) throw new ArgumentOutOfRangeException(nameof(buttons));
        return new LocalizedMessageBox(title, message, buttons);
    }

    public async Task<ButtonResult> ShowWindowDialogAsync(Window owner)
    {
        var confirmation = _buttons == ButtonEnum.YesNo;
        var dialog = new SafetyPromptDialog(_title, _message,
            Loc.Instance.T(confirmation ? "action.no" : "action.ok"),
            confirmation ? Loc.Instance.T("action.yes") : null);
        var allowed = await dialog.ShowAndWaitAsync(owner);
        return confirmation ? allowed ? ButtonResult.Yes : ButtonResult.No : ButtonResult.Ok;
    }
}
