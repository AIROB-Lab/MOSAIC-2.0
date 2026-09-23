using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace MOSAIC.Selector;

public interface IDataTemplateSelector
{
    IDataTemplate? SelectTemplate(object? item, Control? container);
}