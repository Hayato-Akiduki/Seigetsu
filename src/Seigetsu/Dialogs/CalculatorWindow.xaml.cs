using System.Windows;
using System.Windows.Input;
using Seigetsu.Services;
using Wpf.Ui.Controls;

namespace Seigetsu.Dialogs;

/// <summary>電卓（v6 と同じボタンの並び）。開いたまま、ほかの画面も使える。</summary>
public partial class CalculatorWindow : FluentWindow
{
    private static readonly string[] Labels =
    [
        "C", "⌫", "(", ")",
        "7", "8", "9", "÷",
        "4", "5", "6", "×",
        "1", "2", "3", "-",
        "0", ".", "=", "+",
    ];

    public CalculatorWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var label in Labels)
        {
            var button = new Wpf.Ui.Controls.Button
            {
                Content = label,
                FontSize = 18,
                Margin = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Focusable = false, // 押しても、式の欄から入力を続けられるように
                Appearance = label == "=" ? ControlAppearance.Primary : ControlAppearance.Secondary,
            };
            if (label is "C" or "⌫" or "(" or ")" or "÷" or "×" or "-" or "+")
                button.SetResourceReference(ForegroundProperty, "AccentBrush");
            System.Windows.Automation.AutomationProperties.SetName(button, label switch
            {
                "C" => "すべて消す",
                "⌫" => "1 文字消す",
                "÷" => "割る",
                "×" => "掛ける",
                "-" => "引く",
                "+" => "足す",
                "=" => "計算する",
                _ => label,
            });
            button.Click += (_, _) => Press(label);
            Keys.Children.Add(button);
        }
        ExpressionBox.Focus();
    }

    private void Press(string label)
    {
        switch (label)
        {
            case "C":
                ExpressionBox.Clear();
                HistoryText.Text = "";
                ShowHint();
                break;
            case "⌫":
                Backspace();
                break;
            case "=":
                Calculate();
                break;
            default:
                // カーソルの位置に入れる
                ExpressionBox.SelectedText = label;
                ExpressionBox.CaretIndex = ExpressionBox.SelectionStart + ExpressionBox.SelectionLength;
                ExpressionBox.SelectionLength = 0;
                break;
        }
        ExpressionBox.Focus();
    }

    private void Backspace()
    {
        if (ExpressionBox.SelectionLength > 0)
        {
            ExpressionBox.SelectedText = "";
            return;
        }
        var caret = ExpressionBox.CaretIndex;
        if (caret == 0)
            return;
        ExpressionBox.Text = ExpressionBox.Text.Remove(caret - 1, 1);
        ExpressionBox.CaretIndex = caret - 1;
    }

    private void Calculate()
    {
        var expression = ExpressionBox.Text.Trim();
        if (expression == "")
            return;
        try
        {
            var answer = Calculator.Format(Calculator.Evaluate(expression));
            HistoryText.Text = $"{expression} =";
            ExpressionBox.Text = answer;
            ExpressionBox.CaretIndex = answer.Length;
            ShowHint();
        }
        catch (DivideByZeroException)
        {
            ShowError("0 では割れません。");
        }
        catch (FormatException)
        {
            ShowError("式を確認してください。");
        }
    }

    private void ShowHint()
    {
        MessageText.Text = "キーボードでも入力できます（Enter で計算、Esc で閉じる）";
        MessageText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "MutedBrush");
    }

    private void ShowError(string message)
    {
        MessageText.Text = message;
        MessageText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Calculate();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
