// Keep class-level JSON usage beside the model description in ordinary DocFX builds.
exports.postTransform = function (model) {
  if (!model.uid || model.uid.indexOf('MOSAIC.Models.') !== 0) return model;
  var configs = [];
  function isConfig(html) {
    return /<pre><code[^>]*>[\s\S]*?(?:"|&quot;)Type(?:"|&quot;)/.test(html);
  }
  function labelJson(html) {
    return html.replace(/<pre><code[^>]*>([\s\S]*?)<\/code><\/pre>/g, function (all, code) {
      return /(?:"|&quot;)Type(?:"|&quot;)/.test(code)
        ? '<pre><code class="lang-json">' + code + '</code></pre>' : all;
    });
  }
  var examples = model.example || [];
  if (typeof examples === 'string') examples = [examples];
  model.example = examples.filter(function (html) {
    if (!isConfig(html)) return true;
    configs.push(labelJson(html));
    return false;
  });
  // Some models put their configuration in remarks instead of an <example>.
  if (!configs.length && model.remarks) {
    var codeBlocks = model.remarks.match(/<pre><code[^>]*>[\s\S]*?<\/code><\/pre>/g) || [];
    codeBlocks.forEach(function (html) {
      if (isConfig(html)) configs.push(labelJson(html));
    });
  }
  if (configs.length) {
    model.mosaicJsonConfigurations = configs;
    model.mosaicExamplesMoved = examples.length > 0 && !model.example.length;
  }
  return model;
};
