define(["exports","./../dialoghelper/dialoghelper.js","./../layoutmanager.js","./../common/globalize.js","./../loading/loading.js","./../emby-apiclient/connectionmanager.js","./../emby-elements/emby-toggle/emby-toggle.js","./../emby-elements/emby-select/emby-select.js","./../emby-elements/emby-button/emby-button.js","./../emby-elements/emby-button/paper-icon-button-light.js","./../emby-elements/emby-scroller/emby-scroller.js","./../emby-elements/emby-dialogclosebutton/emby-dialogclosebutton.js"],function(_exports,_dialoghelper,_layoutmanager,_globalize,_loading,_connectionmanager,_embyToggle,_embySelect,_embyButton,_paperIconButtonLight,_embyScroller,_embyDialogclosebutton){function onSubmit(e){_loading.default.show();var dlg=e.target.closest(".dialog"),options=this.options,replaceAllMetadata="all"===dlg.querySelector(".selectMetadataRefreshMode").value,mode="FullRefresh",replaceAllImages=dlg.querySelector(".chkReplaceImages").checked,replaceThumbnailImages=dlg.querySelector(".chkReplaceThumbnailImages").checked,options=options.items;return _connectionmanager.default.getApiClient(options[0]).refreshItems(options,{Recursive:!0,ImageRefreshMode:mode,MetadataRefreshMode:mode,ReplaceAllImages:replaceAllImages,ReplaceThumbnailImages:replaceThumbnailImages,ReplaceAllMetadata:replaceAllMetadata}),_dialoghelper.default.close(dlg),function(options){Emby.importModule("./modules/toast/toast.js").then(function(toast){return toast(options)})}(_globalize.default.translate("RefreshingMetadataDots")),_loading.default.hide(),e.preventDefault(),!1}function RefreshDialog(options){this.options=options}
/* D7:refresh-preferences-selection */
function strmOptions() { return window.StrmAssistantRebuildOptions || {}; }
function strmText(en, cn, tw) {
  var locale = _globalize.default.getCurrentLocale().toLowerCase();
  return locale === 'zh-cn' ? cn : /^zh(?:-tw|-hk)?$/.test(locale) ? tw : en;
}
function strmEscape(value) {
  return String(value == null ? '' : value).replace(/[&<>"']/g, function (ch) {
    return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch];
  });
}
function strmRefreshPreferencesHtml() {
  var prefs = String(strmOptions().MetadataRefreshPreferences || '').split(',').filter(Boolean);
  var labels = {
    NoOverview: strmText('Missing overview', '缺少简介', '缺少簡介'),
    NoImage: strmText('Missing image', '缺少图片', '缺少圖片'),
    NonChineseNameOverview: strmText('Non-Chinese name / overview', '非中文标题或简介', '非中文標題或簡介'),
    DefaultEpisodeName: strmText('Default episode name', '默认集名', '預設集名'),
    DefaultSeasonName: strmText('Default season name', '默认季名', '預設季名'),
    ReplaceCapturedImage: strmText('Captured image', '截取的图片', '截取的圖片')
  };
  return '<fieldset data-strmassistant="refresh-preferences"><legend>' + strmText('Strm Assistant refresh preferences', '神医助手刷新偏好', '神醫助手重新整理偏好') + '</legend><div class="fieldset-fields">' +
    '<label class="toggleContainer fieldset-field"><input type="checkbox" is="emby-toggle" class="strmLimitRefresh" /><span>' + strmText('Limit this refresh to matching items', '仅刷新符合条件的条目', '僅重新整理符合條件的項目') + '</span></label>' +
    Object.keys(labels).map(function (p) {
      return '<label class="toggleContainer fieldset-field"><input type="checkbox" is="emby-toggle" class="strmRefreshRule" value="' + p + '"' + (prefs.includes(p) ? ' checked' : '') + ' /><span>' + strmEscape(labels[p]) + '</span></label>';
    }).join('') + '<p class="fieldDescription">' + strmText('Rules select items; they do not exclude metadata fields. No selected rules means all items. Native refresh mode below remains unchanged.', '条件用于选择条目，不代表排除字段。未选条件时包含全部条目；下方原生刷新模式保持不变。', '條件用於選擇項目，不代表排除欄位。未選條件時包含全部項目；下方原生重新整理模式保持不變。') + '</p><p class="strmRefreshStatus fieldDescription" role="status" aria-live="polite"></p></div></fieldset>';
}
function strmMatchesRefresh(item, rules) {
  if (!rules.length) return true;
  var chinese = function (v) { return /[\u3400-\u9fff]/.test(v || ''); }, primary = item.ImageTags && item.ImageTags.Primary;
  return rules.some(function (rule) {
    if (rule === 'NoOverview') return !item.Overview || !item.Overview.trim();
    if (rule === 'NoImage') return !primary;
    if (rule === 'NonChineseNameOverview') return !chinese(item.Name) || !chinese(item.Overview);
    if (rule === 'DefaultEpisodeName') return item.Type === 'Episode' && /^(Episode\s*\d+|第\s*[0-9一二三四五六七八九十百]+\s*集)$/i.test(item.Name || '');
    if (rule === 'DefaultSeasonName') return item.Type === 'Season' && /^(Season\s*\d+|第\s*[0-9一二三四五六七八九十百]+\s*季)$/i.test(item.Name || '');
    if (rule === 'ReplaceCapturedImage') return item.StrmCapturedPrimary === true;
    return false;
  });
}
async function strmAddRefreshCandidate(api, item, rules, found, signal) {
  if (rules.includes('ReplaceCapturedImage') && item.ImageTags && item.ImageTags.Primary) {
    var video = (item.MediaStreams || []).find(function (s) { return s.Type === 'Video' && s.Width > 0 && s.Height > 0; });
    if (video) {
      var images = await api.getJSON(api.getUrl('Items/' + item.Id + '/Images'), signal);
      var primary = images.find(function (image) { return image.ImageType === 'Primary' || image.Type === 'Primary'; });
      item = Object.assign({}, item, { StrmCapturedPrimary: !!primary && primary.Width === video.Width && primary.Height === video.Height });
    }
  }
  if (strmMatchesRefresh(item, rules)) found.set(item.Id, item);
}
async function strmRefreshCandidates(api, inputs, rules, signal) {
  var found = new Map(), user = api.getCurrentUserId(), fields = 'Overview,MediaStreams';
  for (var input of inputs) {
    var full = await api.getItem(user, input.Id, { Fields: fields }, signal);
    if (full.IsFolder) {
      var offset = 0;
      do {
        if (signal && signal.aborted) throw Object.assign(new Error('Aborted'), { name: 'AbortError' });
        var response = await api.getItems(user, { ParentId: full.Id, Recursive: true, IncludeItemTypes: 'Movie,Series,Season,Episode,Video', Fields: fields, StartIndex: offset, Limit: 200, EnableTotalRecordCount: true }, signal);
        var items = response.Items || [];
        for (var item of items) await strmAddRefreshCandidate(api, item, rules, found, signal);
        offset += items.length;
        if (offset > 5000 || found.size > 5000) throw new Error(strmText('Select a smaller set of items (maximum 5,000).', '请缩小选择范围（最多 5,000 条）。', '請縮小選擇範圍（最多 5,000 項）。'));
        if (items.length < 200 || offset >= response.TotalRecordCount) break;
      } while (true);
    } else await strmAddRefreshCandidate(api, full, rules, found, signal);
  }
  return Array.from(found.values());
}
async function strmDispatchRefresh(api, items, parameters, signal, status) {
  var sent = 0, failed = 0;
  for (var offset = 0; offset < items.length; offset += 8) {
    if (signal.aborted) throw Object.assign(new Error('Aborted'), { name: 'AbortError' });
    var results = await Promise.allSettled(items.slice(offset, offset + 8).map(function (item) { return api.refreshItems([item], parameters); }));
    results.forEach(function (result) { if (result.status === 'fulfilled') sent++; else failed++; });
    if (status) status.textContent = strmText('Refresh requests sent: ', '已发送刷新请求：', '已傳送重新整理請求：') + sent + '/' + items.length + '; ' + strmText('failed: ', '失败：', '失敗：') + failed;
  }
  if (failed) throw new Error(strmText('Some refresh requests failed: ', '部分刷新请求失败：', '部分重新整理請求失敗：') + failed + '/' + items.length);
}
function strmSubmitRefresh(e) {
  e.preventDefault();
  var dlg = e.target.closest('.dialog'), inputs = JSON.parse(JSON.stringify(this.options.items)), api = _connectionmanager.default.getApiClient(inputs[0]);
  if (dlg.strmRefreshBusy) return false;
  var selected = dlg.querySelector('.strmLimitRefresh'), limit = selected && selected.checked;
  var rules = Array.prototype.filter.call(dlg.querySelectorAll('.strmRefreshRule'), function (x) { return x.checked; }).map(function (x) { return x.value; });
  var parameters = { Recursive: !limit, ImageRefreshMode: 'FullRefresh', MetadataRefreshMode: 'FullRefresh', ReplaceAllImages: dlg.querySelector('.chkReplaceImages').checked, ReplaceThumbnailImages: dlg.querySelector('.chkReplaceThumbnailImages').checked, ReplaceAllMetadata: dlg.querySelector('.selectMetadataRefreshMode').value === 'all' };
  strmRefreshIntroParameters(dlg, inputs, limit, parameters);
  var abort = new AbortController(), button = dlg.querySelector('.btnSubmit');
  dlg.addEventListener('close', function () { abort.abort(); }, { once: true });
  dlg.strmRefreshBusy = true; if (button) button.disabled = true; _loading.default.show();
  var work = limit ? strmRefreshCandidates(api, inputs, rules, abort.signal) : Promise.resolve(inputs);
  dlg.strmRefreshPromise = work.then(function (items) {
    if (!items.length) throw new Error(strmText('No items match the selected rules.', '没有符合所选条件的条目。', '沒有符合所選條件的項目。'));
    return limit ? strmDispatchRefresh(api, items, parameters, abort.signal, dlg.querySelector('.strmRefreshStatus')) : api.refreshItems(items, parameters);
  }).then(function () { _dialoghelper.default.close(dlg); return Emby.importModule('./modules/toast/toast.js').then(function (toast) { toast(_globalize.default.translate('RefreshingMetadataDots')); }); })
    .catch(function (error) { if (error && error.name === 'AbortError') return; return Emby.importModule('./modules/toast/toast.js').then(function (toast) { toast(error.message || _globalize.default.translate('Error')); }); })
    .finally(function () { dlg.strmRefreshBusy = false; if (button) button.disabled = false; _loading.default.hide(); });
  return false;
}

/* D105:refresh-intro-start */
function strmRefreshIntroRoot(items) {
  var item = items && items.length === 1 && items[0];
  return item && typeof item.Id === 'string' && item.Id.trim() && (item.Type === 'Series' || item.Type === 'Season' && Number.isFinite(item.IndexNumber) && item.IndexNumber >= 1) ? item : null;
}
function strmRefreshIntroControl(dlg, items) {
  var root = strmRefreshIntroRoot(items), anchor = dlg.querySelector('.fldReplaceExistingThumbnailImages');
  if (strmOptions().ReplaceIntroMarkersSupported !== true || !root || !anchor) return;
  dlg.strmRefreshIntroRoot = { Id: root.Id, Type: root.Type, IndexNumber: root.IndexNumber };
  anchor.insertAdjacentHTML('afterend', '<div class="toggleContainer fldReplaceIntroMarkers"><label><input type="checkbox" is="emby-toggle" class="chkReplaceIntroMarkers" /><span>' +
    strmText('Replace Intro Markers', '替换现有片头标记', '替换現有片頭標記') + '</span></label><div class="toggleFieldDescription fieldDescription">' +
    strmText('After metadata refresh, rebuild fingerprints and replace intro markers when a new result is available. The rebuild step preserves other chapters; metadata refresh follows server rules. Fingerprint detection must be enabled for this library.', '元数据刷新后重新生成指纹；有新结果时替换片头。重建步骤保留其它章节，元数据刷新本身遵循服务器规则。请先启用该媒体库的指纹检测。', '中繼資料重新整理後重新產生指紋；有新結果時替換片頭。重建步驟保留其它章節，中繼資料重新整理本身遵循伺服器規則。請先啟用此媒體庫的指紋偵測。') + '</div><div class="toggleFieldDescription fieldDescription strmRefreshIntroLimitHelp">' +
    strmText('Intro rebuild requires refreshing the whole series or season', '重建片头需要刷新整部剧集或整季', '重建片頭需要重新整理整部影集或整季') + '</div></div>');
  var limit = dlg.querySelector('.strmLimitRefresh'), checkbox = dlg.querySelector('.chkReplaceIntroMarkers');
  function sync() { checkbox.disabled = !!(limit && limit.checked); if (checkbox.disabled) checkbox.checked = false; }
  if (limit) limit.addEventListener('change', sync);
  sync();
}
function strmRefreshIntroParameters(dlg, items, limit, parameters) {
  var root = strmRefreshIntroRoot(items), bound = dlg.strmRefreshIntroRoot, checkbox = dlg.querySelector('.chkReplaceIntroMarkers');
  if (!limit && strmOptions().ReplaceIntroMarkersSupported === true && root && bound && root.Id === bound.Id && root.Type === bound.Type && root.IndexNumber === bound.IndexNumber && checkbox && checkbox.checked === true && !checkbox.disabled && !checkbox.hidden && !checkbox.closest('.hide,[hidden]')) parameters.ReplaceIntroMarkers = true;
}
/* D105:refresh-intro-end */

Object.defineProperty(_exports,"__esModule",{value:!0}),_exports.default=void 0,RefreshDialog.prototype.show=function(){var dialogOptions={removeOnClose:!0,scrollY:!1},dlg=(_layoutmanager.default.tv?dialogOptions.size="fullscreen":dialogOptions.size="small",_dialoghelper.default.createDialog(dialogOptions)),dialogOptions=(dlg.classList.add("formDialog"),""),instance=(dialogOptions=(dialogOptions=(dialogOptions=(dialogOptions=dialogOptions+'<div class="formDialogHeader">'+'<button type="button" is="emby-dialogclosebutton"></button>')+'<h3 class="formDialogHeaderTitle">'+_globalize.default.translate("HeaderRefreshMetadata"))+"</h3>"+"</div>")+function(){var html="";return html+'<div is="emby-scroller" data-horizontal="false" data-focusscroll="true" class="formDialogContent">'+'<div class="scrollSlider">'+'<form class="dialogContentInner dialog-content-centered padded-left padded-right">'+strmRefreshPreferencesHtml()+'<div class="fldSelectPlaylist selectContainer">'+('<select is="emby-select" class="selectMetadataRefreshMode" label="'+_globalize.default.translate("LabelRefreshMode")+'">')+('<option value="missing">'+_globalize.default.translate("SearchForMissingMetadata")+"</option>")+('<option value="all" selected>'+_globalize.default.translate("ReplaceAllMetadata")+"</option>")+"</select>"+'<div class="fieldDescription">'+_globalize.default.translate("RefreshDialogHelp")+"</div>"+"</div>"+'<div class="toggleContainer hide fldReplaceExistingImages">'+"<label>"+'<input type="checkbox" is="emby-toggle" class="chkReplaceImages" />'+("<span>"+_globalize.default.translate("ReplaceExistingImages")+"</span>")+"</label>"+'<div class="toggleFieldDescription fieldDescription">'+_globalize.default.translate("ReplaceExistingImagesHelp")+"</div>"+"</div>"+'<div class="toggleContainer hide fldReplaceExistingThumbnailImages">'+"<label>"+'<input type="checkbox" is="emby-toggle" class="chkReplaceThumbnailImages" />'+("<span>"+_globalize.default.translate("ReplaceExistingThumbnailImages")+"</span>")+"</label>"+'<div class="toggleFieldDescription fieldDescription">'+_globalize.default.translate("ReplaceExistingThumbnailImagesHelp")+"</div>"+"</div>"+"<br />"+'<div class="formDialogFooter">'+('<button is="emby-button" type="submit" class="raised btnSubmit block formDialogFooterItem button-submit">'+_globalize.default.translate("Refresh")+"</button>")+"</div>"+"</form>"+"</div>"+"</div>"}(),dlg.innerHTML=dialogOptions,strmRefreshIntroControl(dlg,this.options.items),dlg.querySelector("form").addEventListener("submit",strmSubmitRefresh.bind(this)),this);return dlg.querySelector(".selectMetadataRefreshMode").addEventListener("change",function(){dlg.querySelector(".fldReplaceExistingImages").classList.remove("hide"),_connectionmanager.default.getApiClient(instance.options.items[0]).isMinServerVersion("4.9.1.1")&&dlg.querySelector(".fldReplaceExistingThumbnailImages").classList.remove("hide")}),this.options.mode&&(dlg.querySelector(".selectMetadataRefreshMode").value=this.options.mode),dlg.querySelector(".selectMetadataRefreshMode").dispatchEvent(new CustomEvent("change")),new Promise(function(resolve,reject){dlg.addEventListener("close",resolve),_dialoghelper.default.open(dlg)})};_exports.default=RefreshDialog});