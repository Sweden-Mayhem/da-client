#region
using Chaos.Client.Controls.Components;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.LobbyLogin;

public sealed class PasswordChangeControl : SlideFadePanel
{
    public UIButton? CancelButton { get; }
    public UITextBox? ConfirmPasswordField { get; }
    public UITextBox? CurrentPasswordField { get; }
    public UITextBox? NameField { get; }
    public UITextBox? NewPasswordField { get; }
    public UIButton? OkButton { get; }

    public PasswordChangeControl()
        : base("_npw")
    {
        Name = "PasswordChange";
        Visible = false;
        UsesControlStack = true;

        //buttons
        OkButton = CreateButton("OK");
        CancelButton = CreateButton("Cancel");

        if (OkButton is not null)
            OkButton.Clicked += () => OnOk?.Invoke();

        if (CancelButton is not null)
            CancelButton.Clicked += () => OnCancel?.Invoke();

        //text fields, type 7 with 0 images, manually created
        NameField = CreateTextBox("Name");
        CurrentPasswordField = CreateTextBox("Password", 24);
        NewPasswordField = CreateTextBox("NewPassword", 24);
        ConfirmPasswordField = CreateTextBox("Confirm", 24);
        NameField?.Native(8);
        CurrentPasswordField?.Native(8);
        NewPasswordField?.Native(8);
        ConfirmPasswordField?.Native(8);

        //nudge the fields down 1px and shrink them 4px in height
        NudgeField(NameField);
        NudgeField(CurrentPasswordField);
        NudgeField(NewPasswordField);
        NudgeField(ConfirmPasswordField);

        NameField?.ForegroundColor = LegendColors.White;
        NameField?.IsTabStop = true;
        
        CurrentPasswordField?.ForegroundColor = LegendColors.White;
        CurrentPasswordField?.IsMasked = true;
        CurrentPasswordField?.IsTabStop = true;
        
        NewPasswordField?.ForegroundColor = LegendColors.White;
        NewPasswordField?.IsMasked = true;
        NewPasswordField?.IsTabStop = true;
        
        ConfirmPasswordField?.ForegroundColor = LegendColors.White;
        ConfirmPasswordField?.IsMasked = true;
        ConfirmPasswordField?.IsTabStop = true;

        //focus management
        if (NameField is not null)
            NameField.OnFocused += OnTextBoxFocused;

        if (CurrentPasswordField is not null)
            CurrentPasswordField.OnFocused += OnTextBoxFocused;

        if (NewPasswordField is not null)
            NewPasswordField.OnFocused += OnTextBoxFocused;

        if (ConfirmPasswordField is not null)
            ConfirmPasswordField.OnFocused += OnTextBoxFocused;
    }

    private void ClearFields()
    {
        UITextBox?[] fields =
        [
            NameField,
            CurrentPasswordField,
            NewPasswordField,
            ConfirmPasswordField
        ];

        foreach (var field in fields)
        {
            if (field is null)
                continue;

            field.IsFocused = false;
            field.Text = string.Empty;
        }
    }

    public override void Hide()
    {
        ClearFields();
        base.Hide();
    }

    public event CancelHandler? OnCancel;

    public event OkHandler? OnOk;

    private void OnTextBoxFocused(UITextBox focused)
    {
        UITextBox?[] fields =
        [
            NameField,
            CurrentPasswordField,
            NewPasswordField,
            ConfirmPasswordField
        ];

        foreach (var field in fields)
            if (field is not null && (field != focused))
                field.IsFocused = false;
    }

    public override void Show()
    {
        ClearFields();

        NameField?.IsFocused = true;

        base.Show();
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        switch (e.Key)
        {
            case Keys.Tab:
                //cycle focus through the 4 fields, name then current password then new password then confirm then back to name
                if (NameField?.IsFocused == true)
                {
                    NameField.IsFocused = false;
                    CurrentPasswordField?.IsFocused = true;
                } else if (CurrentPasswordField?.IsFocused == true)
                {
                    CurrentPasswordField.IsFocused = false;
                    NewPasswordField?.IsFocused = true;
                } else if (NewPasswordField?.IsFocused == true)
                {
                    NewPasswordField.IsFocused = false;
                    ConfirmPasswordField?.IsFocused = true;
                } else
                {
                    ConfirmPasswordField?.IsFocused = false;
                    NameField?.IsFocused = true;
                }

                e.Handled = true;

                break;

            case Keys.Enter:
                OkButton?.PerformClick();
                e.Handled = true;

                break;

            case Keys.Escape:
                CancelButton?.PerformClick();
                e.Handled = true;

                break;
        }
    }
}