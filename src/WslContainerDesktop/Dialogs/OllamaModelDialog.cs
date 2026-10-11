// WSL Container Desktop - a WinUI 3 manager for WSL containers.
// Copyright (C) 2026 Michael Hacker
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Dialogs;

/// <summary>
/// Chooses the model installed alongside a newly deployed Ollama. Without one the chat UI starts
/// with nothing to talk to, so the template asks up front instead of leaving it half-finished.
/// </summary>
public sealed class OllamaModelDialog : ContentDialog
{
    private readonly ComboBox _model;
    private readonly TextBlock _error;

    /// <summary>Creates a new &lt;c&gt;OllamaModelDialog&lt;/c&gt; and wires the state used by the dialog or model.</summary>
    public OllamaModelDialog()
    {
        Title = UiText.Get("Workload_Text_Choose_a_model_78a7ab", "Choose a model");
        PrimaryButtonText = UiText.Get("Workload_Text_Download_model_9979be", "Download model");
        SecondaryButtonText = UiText.Get("Workload_Text_Skip_for_now_b58eb5", "Skip for now");
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        _model = new ComboBox
        {
            Header = UiText.Get("Workload_Text_Model_5e2c61", "Model"),
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 320,
        };
        foreach (var suggestion in OpenWebUiPlanner.SuggestedModels)
        {
            _model.Items.Add(suggestion);
        }

        // Select after populating: an editable ComboBox re-derives its text from the item list,
        // so a Text set beforehand is discarded and the wrong suggestion ends up displayed.
        _model.SelectedItem = OpenWebUiPlanner.RecommendedModel;
        _model.Text = OpenWebUiPlanner.RecommendedModel;

        _error = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        Content = new StackPanel
        {
            Spacing = 12,
            Width = 420,
            Children =
            {
                new TextBlock
                {
                    Text = UiText.Get("Workload_Text_Open_WebUI_needs_a_model_to_121755", "Open WebUI needs a model to chat with. {0} is a good starting point — small and quick to download. You can type any Ollama model name instead, or add more later from the web UI.", OpenWebUiPlanner.RecommendedModel),
                    TextWrapping = TextWrapping.Wrap,
                },
                _model,
                _error,
            },
        };

        PrimaryButtonClick += (_, args) =>
        {
            if (OpenWebUiPlanner.IsValidModelName(Model))
            {
                return;
            }

            // Keep the dialog open rather than failing later inside the container.
            args.Cancel = true;
            _error.Text = UiText.Get("Workload_Text_Enter_a_model_name_like_llama3_283218", "Enter a model name like llama3.2:3b or qwen2.5:7b.");
            _error.Visibility = Visibility.Visible;
        };
    }

    /// <summary>Gets the model.</summary>
    public string Model => (_model.Text ?? string.Empty).Trim();
}
