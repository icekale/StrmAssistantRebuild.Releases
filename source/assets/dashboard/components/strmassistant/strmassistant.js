define(['connectionManager', 'globalize', 'loading', 'toast', 'confirm', 'dialog'], function (connectionManager, globalize, loading, toast, confirm, dialog) {

    return {
        copy: function (libraryId) {
            loading.show();

            let apiClient = connectionManager.currentApiClient();
            let copyApi = apiClient.getUrl('Library/VirtualFolders/Copy');

            apiClient.ajax({
                type: "POST",
                url: copyApi,
                data: JSON.stringify({ Id: libraryId }),
                contentType: "application/json"
            }).finally(() => {
                loading.hide();
                const locale = globalize.getCurrentLocale().toLowerCase();
                const confirmMessage = (locale === 'zh-cn') ? '\u590d\u5236\u5a92\u4f53\u5e93\u6210\u529f' : 
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u8907\u88fd\u5a92\u9ad4\u5eab\u6210\u529f' : 'Copy Library Success');
                toast(confirmMessage);
                const itemsContainer = document.querySelector('.view-librarysetup-library .itemsContainer, .view-librarysetup-librarysetup .itemsContainer');
                if (itemsContainer) {
                    itemsContainer.notifyRefreshNeeded(true);
                }
            });
        },

        remove: function (libraryId, libraryName) {
            confirm({
                text: globalize.translate('MessageAreYouSureYouWishToRemoveLibrary').replace('{0}', libraryName),
                title: globalize.translate('HeaderRemoveLibrary'),
                confirmText: globalize.translate('Remove'),
                primary: 'cancel'
            })
            .then(function() {
                loading.show();

                let apiClient = connectionManager.currentApiClient();
                let deleteApi = apiClient.getUrl('Library/VirtualFolders/Delete');

                apiClient.ajax({
                    type: "POST",
                    url: deleteApi + "?refreshLibrary=false&id=" + libraryId,
                    data: {},
                    contentType: "application/json"
                }).finally(() => {
                    loading.hide();
                    const locale = globalize.getCurrentLocale().toLowerCase();
                    const confirmMessage = (locale === 'zh-cn') ? '\u5408\u96c6\u5220\u9664\u6210\u529f' : 
                        (['zh-hk', 'zh-tw'].includes(locale) ? '\u5408\u96C6\u5236\u9662\u6210\u529F' : 'Delete Collections Success');
                    toast(confirmMessage);
                    const itemsContainer = document.querySelector('.view-librarysetup-library .itemsContainer, .view-librarysetup-librarysetup .itemsContainer');
                    if (itemsContainer) {
                        itemsContainer.notifyRefreshNeeded(true);
                    }
                });
            });
        },

        traverse: function (itemId) {
            loading.show();

            let apiClient = connectionManager.currentApiClient();
            let scanApi = apiClient.getUrl(`Items/${itemId}/Refresh`);
            let queryParams = {
                Recursive: true,
                ImageRefreshMode: 'Default',
                MetadataRefreshMode: 'Default',
                ReplaceAllImages: false,
                ReplaceAllMetadata: false
            };
            let queryString = new URLSearchParams(queryParams).toString();

            apiClient.ajax({
                type: "POST",
                url: `${scanApi}?${queryString}`,
                data: {},
                contentType: "application/json"
            }).finally(() => {
                loading.hide();
                const confirmMessage = globalize.translate('ScanningLibraryFilesDots');
                toast(confirmMessage);
            });
        },

        /* D93:delperson-start */
        delperson: async function strmDeletePerson(itemId, itemName) {
  const escapedName = String(itemName == null ? '' : itemName).replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
  await confirm({
    text: globalize.translate('ConfirmDeleteItems') + '\n\n' + itemName + '\n\n' + globalize.translate('AreYouSureToContinue'),
    html: globalize.translate('ConfirmDeleteItems') + '<p><div class="secondaryText">' + escapedName + '</div></p>' + '<p style="margin-bottom:0;">' + globalize.translate('AreYouSureToContinue') + '</p>',
    title: globalize.translate('HeaderDeleteItem'),
    confirmText: globalize.translate('Delete'),
    primary: 'cancel',
    centerText: false
  });
  loading.show();
  let result;
  try {
    const apiClient = connectionManager.currentApiClient();
    result = await apiClient.ajax({ type: 'POST', url: apiClient.getUrl(`Items/${itemId}/DeletePerson`), data: {}, contentType: 'application/json' });
  } finally {
    loading.hide();
  }
  const locale = globalize.getCurrentLocale().toLowerCase();
  toast(locale === 'zh-cn' ? '\u5220\u9664\u4eba\u5458\u6210\u529f' : ['zh-hk', 'zh-tw'].includes(locale) ? '\u524a\u9664\u4eba\u54e1\u6210\u529f' : 'Delete Person Success');
  const itemsContainer = document.querySelector('.peopleItemsContainer');
  if (itemsContainer) itemsContainer.notifyRefreshNeeded(true);
  return result;
},
        /* D93:delperson-end */

        delver: function (itemId, itemName, itemType) {
            if (itemType === 'Movie') {
                confirm({
                    text: globalize.translate('ConfirmDeleteItems') + "\n\n" +
                            itemName + "\n\n" +
                            globalize.translate('AreYouSureToContinue'),
                    html: globalize.translate('ConfirmDeleteItems') +
                            '<p><div class="secondaryText">' + itemName + "</div></p>" +
                            '<p style="margin-bottom:0;">' + globalize.translate('AreYouSureToContinue') + "</p>",
                    title: globalize.translate('HeaderDeleteItem'),
                    confirmText: globalize.translate('Delete'),
                    primary: 'cancel',
                    centerText: !1
                })
                .then(function() {
                    deleteVersion(itemId);
                });
            } else {
                const locale = globalize.getCurrentLocale().toLowerCase();
                const deleteEpisode = (locale.startsWith('zh') || locale.startsWith('ja') || locale.startsWith('ko'))
                            ? globalize.translate('Delete') + globalize.translate('Episode')
                            : globalize.translate('Delete') + ' ' + globalize.translate('Episode');
                const deleteSeason = (locale.startsWith('zh') || locale.startsWith('ja') || locale.startsWith('ko'))
                            ? globalize.translate('Delete') + globalize.translate('Season')
                            : globalize.translate('Delete') + ' ' + globalize.translate('Season');
                dialog({
                    text: globalize.translate('ConfirmDeleteItems') + "\n\n" +
                            itemName + "\n\n" +
                            globalize.translate('AreYouSureToContinue'),
                    html: globalize.translate('ConfirmDeleteItems') +
                            '<p><div class="secondaryText">' + itemName + "</div></p>" +
                            '<p style="margin-bottom:0;">' + globalize.translate('AreYouSureToContinue') + "</p>",
                    title: globalize.translate('HeaderDeleteItem'),
                    buttons: [
                        { name: globalize.translate("Cancel"), id: "cancel", type: "submit" },
                        { name: deleteEpisode, id: "deleteepisode", type: "cancel" },
                        { name: deleteSeason, id: "deleteseason", type: "cancel" }
                    ],
                    centerText: !1
                })
                .then(function(id) {
                    if (id === 'deleteepisode') {
                        deleteVersion(itemId);
                    } else if (id === 'deleteseason') {
                        deleteVersion(itemId, true);
                    }
                });
            }
            function deleteVersion(itemId, deleteParent = false) {
                loading.show();
                let apiClient = connectionManager.currentApiClient();
                let deleteApi = apiClient.getUrl(`Items/${itemId}/DeleteVersion${deleteParent ? `?DeleteParent=true` : ''}`);
                apiClient.ajax({
                    type: "POST",
                    url: deleteApi,
                    data: {},
                    contentType: "application/json"
                }).finally(() => {
                    loading.hide();
                    const locale = globalize.getCurrentLocale().toLowerCase();
                    const confirmMessage = (locale === 'zh-cn') ? '\u5220\u9664\u7248\u672C\u6210\u529F' : 
                        (['zh-hk', 'zh-tw'].includes(locale) ? '\u524A\u9664\u7248\u672C\u6210\u529F' : 'Delete Version Success');
                    toast(confirmMessage);
                });
            }
        },

        lock: function (itemId, lockData) {
            let apiClient = connectionManager.currentApiClient();
            let lockApi = apiClient.getUrl(`Items/${itemId}/Lock`);
            let queryParams = {
                LockData: lockData
            };
            let queryString = new URLSearchParams(queryParams).toString();

            apiClient.ajax({
                type: "POST",
                url: `${lockApi}?${queryString}`,
                data: {},
                contentType: "application/json"
            });
        },

        /* D94:clear-mediainfo-start */
        clear_mediainfo: async function strmClearMediaInfo(itemId) {
  if (typeof itemId !== 'string' || !itemId.trim()) throw new Error('An item Id is required.');
  const apiClient = connectionManager.currentApiClient();
  const result = await apiClient.ajax({ type: 'POST', url: apiClient.getUrl(`Items/${encodeURIComponent(itemId)}/ClearMediaInfo`), data: {}, contentType: 'application/json', dataType: 'text' });
  if (result != null && result !== '') {
    const progress = typeof result === 'string' ? JSON.parse(result) : result;
    if (!progress || !Number.isSafeInteger(progress.Total) || progress.Total < 0 || progress.Done !== progress.Total || progress.Failed !== 0 || progress.Cancelled !== false || !Array.isArray(progress.Errors) || progress.Errors.length) {
      const locale = globalize.getCurrentLocale().toLowerCase();
      const counts = progress && [progress.Done, progress.Total, progress.Failed].every(value => Number.isSafeInteger(value) && value >= 0) && typeof progress.Cancelled === 'boolean';
      const message = locale === 'zh-cn' ? '清除未完成（完成/总数；失败；已取消）：' : ['zh-hk', 'zh-tw'].includes(locale) ? '清除未完成（完成/總數；失敗；已取消）：' : 'Clear incomplete (done/total; failed; cancelled): ';
      throw Object.assign(new Error(counts ? message + progress.Done + '/' + progress.Total + '; ' + progress.Failed + '; ' + progress.Cancelled : globalize.translate('Error')), { batchResult: progress });
    }
  }
  return result;
},
        /* D94:clear-mediainfo-end */

        clear_intro: function (itemId) {
            const locale = globalize.getCurrentLocale().toLowerCase();
            const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7247\u5934\u6807\u8BB0' : 
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7247\u982D\u6A19\u8A18' : 'Clear Intro Markers');
            confirm({
                text: globalize.translate('AreYouSureToContinue'),
                title: commandName,
                confirmText: globalize.translate('Clear'),
                primary: 'cancel'
            })
            .then(function() {
                loading.show();
                let apiClient = connectionManager.currentApiClient();
                let clearIntroApi = apiClient.getUrl(`Items/${itemId}/ClearIntro`);
                apiClient.ajax({
                    type: "POST",
                    url: clearIntroApi,
                    data: {},
                    contentType: "application/json"
                }).finally(() => {
                    loading.hide();
                    const confirmMessage = (locale === 'zh-cn') ? commandName + '\u6210\u529F' : 
                        (['zh-hk', 'zh-tw'].includes(locale) ? commandName + '\u6210\u529F' : commandName + ' Success');
                    toast(confirmMessage);
                });
            });
        }
        /* D94:clear-chapter-image-start */
        ,clear_chapter_image: async function strmClearChapterImage(itemId) {
  if (typeof itemId !== 'string' || !itemId.trim()) throw new Error('An item Id is required.');
  const apiClient = connectionManager.currentApiClient();
  const result = await apiClient.ajax({ type: 'POST', url: apiClient.getUrl(`Items/${encodeURIComponent(itemId)}/ClearChapterImage`), data: {}, contentType: 'application/json', dataType: 'text' });
  if (result != null && result !== '') {
    const progress = typeof result === 'string' ? JSON.parse(result) : result;
    if (!progress || !Number.isSafeInteger(progress.Total) || progress.Total < 0 || progress.Done !== progress.Total || progress.Failed !== 0 || progress.Cancelled !== false || !Array.isArray(progress.Errors) || progress.Errors.length) {
      const locale = globalize.getCurrentLocale().toLowerCase();
      const counts = progress && [progress.Done, progress.Total, progress.Failed].every(value => Number.isSafeInteger(value) && value >= 0) && typeof progress.Cancelled === 'boolean';
      const message = locale === 'zh-cn' ? '清除未完成（完成/总数；失败；已取消）：' : ['zh-hk', 'zh-tw'].includes(locale) ? '清除未完成（完成/總數；失敗；已取消）：' : 'Clear incomplete (done/total; failed; cancelled): ';
      throw Object.assign(new Error(counts ? message + progress.Done + '/' + progress.Total + '; ' + progress.Failed + '; ' + progress.Cancelled : globalize.translate('Error')), { batchResult: progress });
    }
  }
  return result;
}
        /* D94:clear-chapter-image-end */
    };
});
