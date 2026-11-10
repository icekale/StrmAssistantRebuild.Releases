define(["exports","./commandprocessor.js","./common/inputmanager.js","./emby-apiclient/connectionmanager.js","./common/playback/playbackmanager.js","./common/itemmanager/itemmanager.js","./layoutmanager.js"],function(_exports,_commandprocessor,_inputmanager,_connectionmanager,_playbackmanager,_itemmanager,_layoutmanager){function getItemElementFromChildNode(child,isMainElement,itemsContainer){itemsContainer=(itemsContainer=null==itemsContainer?void 0:itemsContainer.getItemSelector())||".card,.listItem,.epgRow,.dataGridItem";return isMainElement?child.closest(itemsContainer):child.closest("[data-type],"+itemsContainer)}function getItemFromElement(element,itemsContainer){var item;return item=(itemsContainer=itemsContainer||element.closest(".itemsContainer"))&&(item=itemsContainer.getItemFromElement(element))?item:{Type:element.getAttribute("data-type"),Id:element.getAttribute("data-id"),ServerId:element.getAttribute("data-serverid"),IsFolder:"true"===element.getAttribute("data-isfolder"),Status:element.getAttribute("data-status")||null,MediaType:element.getAttribute("data-mediatype")||null,ChannelId:element.getAttribute("data-channelid")||null,TimerId:element.getAttribute("data-timerid")||null,SeriesTimerId:element.getAttribute("data-seriestimerid")||null}}function showContextMenu(itemElement,options){options.itemsContainer||(options.itemsContainer=itemElement.closest(".itemsContainer"));var itemsContainer=options.itemsContainer;return Promise.all([function(button,itemsContainer){var itemFromElement=getItemFromElement(button=getItemElementFromChildNode(button,null,itemsContainer),itemsContainer),button=itemFromElement.Type;if(!_itemmanager.default.getItemController(button).isSingleItemFetchRequired(button))return Promise.resolve(itemFromElement);var id=itemFromElement.Id;if(!id)return Promise.resolve(itemFromElement);var apiClient=_connectionmanager.default.getApiClient(itemFromElement);switch(button){case"VirtualFolder":return function(apiClient,id){return apiClient.getVirtualFolders().then(function(result){return result.Items.filter(function(u){return u.ItemId===id})[0]})}(apiClient,id);case"User":return apiClient.getUser(id);case"Timer":return apiClient.getLiveTvTimer(id);case"SeriesTimer":return apiClient.getLiveTvSeriesTimer(id)}return(itemsContainer=["ShareLevel"]).push("SyncStatus"),itemsContainer.push("ContainerSyncStatus"),apiClient.getItem(apiClient.getCurrentUserId(),id,{fields:itemsContainer.join(","),ExcludeFields:"Chapters,Overview,People,MediaStreams,Subviews"}).then(function(fullItem){return fullItem.PlaylistItemId=itemFromElement.PlaylistItemId,fullItem.CollectionId=itemFromElement.CollectionId,fullItem.PlaylistId=itemFromElement.PlaylistId,fullItem.ItemIdInList=itemFromElement.ItemIdInList,fullItem})}(itemElement,itemsContainer),Emby.importModule("./modules/itemcontextmenu.js")]).then(function(responses){var item=responses[0];return function(item){return null!=(item=_connectionmanager.default.getApiClient(item))&&item.getCurrentUserId()?item.getCurrentUser():Promise.resolve(null)}(item).then(function(user){options.positionTo&&!options.itemElement&&(options.itemElement=itemElement);var commandOptions=Object.assign({},itemsContainer.getCommandOptions(item));return itemsContainer.pause(),responses[1].show(Object.assign(commandOptions,{items:[item],play:!0,queue:!0,playAllFromHere:!item.IsFolder,queueAllFromHere:!item.IsFolder,user:user,multiSelect:((itemsContainer.currentListOptions||{}).options||{}).multiSelect,programInfo:!0},options)).then(function(res){return itemsContainer.resume({}),Promise.resolve(res)},function(err){return itemsContainer.resume({}),Promise.reject(err)})})})}function notifyItemsContainerOfCommandResult(itemsContainer,result){itemsContainer.onCommandResult(result)}function executeAction(originalEvent,itemElement,itemsContainer,target,action){if(target=target||itemElement,!(itemElement=getItemElementFromChildNode(itemElement,null,itemsContainer)))return Promise.resolve();var item=getItemFromElement(itemElement,itemsContainer);if("custom"!==action){var options={positionTo:target,itemElement:itemElement,itemsContainer:itemsContainer,eventType:originalEvent.type,eventTarget:originalEvent.target};switch(action){case"togglecheckbox":if(null!=originalEvent&&originalEvent.target.closest(".dragHandle"))return;break;case"menu":case"info":originalEvent&&"click"===originalEvent.type?(options.positionY=_layoutmanager.default.tv?"top":"bottom",options.positionX=_layoutmanager.default.tv?"right":"after"):null!=(_originalEvent$detail=originalEvent.detail)&&_originalEvent$detail.originalEvent&&(options.positionY=_layoutmanager.default.tv?"top":"bottom",options.positionX="after",options.positionClientX=originalEvent.detail.originalEvent.clientX,options.positionClientY=originalEvent.detail.originalEvent.clientY);var _originalEvent$detail=function(itemsContainer){return function(result){return itemsContainer&&notifyItemsContainerOfCommandResult(itemsContainer,result),Promise.resolve(result)}}(itemsContainer);return showContextMenu(itemElement,options).then(_originalEvent$detail),Promise.resolve();case"link":itemsContainer&&(options.context=((itemsContainer.currentListOptions||{}).options||{}).context)}target=function(command,itemsContainer){return function(result){return itemsContainer&&notifyItemsContainerOfCommandResult(itemsContainer,{command:command,result:result}),Promise.resolve(result)}}(action,itemsContainer);return _commandprocessor.default.executeCommand(action,[item],options).then(target)}itemElement.dispatchEvent(new CustomEvent("action-null",{detail:{item:item,originalEvent:originalEvent},cancelable:!1,bubbles:!0})),Promise.resolve()}function onClick(e){var target=e.target,itemElement=target.closest(".itemAction");if(itemElement){var actionElement=itemElement,action=actionElement.getAttribute("data-action");if(action||(actionElement=actionElement.closest("[data-action]"))&&(action=actionElement.getAttribute("data-action")),action){var itemsContainer=target.closest(".itemsContainer");switch(action){case"openlink":case"default":case"none":break;default:executeAction(e,itemElement,itemsContainer,actionElement,action)}switch(action){case"multiselect":case"openlink":case"toggleitemchecked":break;default:return"default"!==action&&e.preventDefault(),e.stopPropagation(),!1}}}}function onCommand(e){var scroller,cmd=e.detail.command;switch(cmd){case"play":case"playpause":var target,itemElement,itemsContainer=(target=e.target).closest(".itemsContainer");!(itemElement=getItemElementFromChildNode(target,null,itemsContainer))||"true"===(null==itemsContainer?void 0:itemsContainer.getAttribute("data-skipplaycommands"))||_playbackmanager.default.isPlayingMediaType(["Audio","Video"])&&!_playbackmanager.default.isBackgroundPlayback()||(e.preventDefault(),e.stopPropagation(),executeAction(e,itemElement,itemsContainer,itemElement,cmd));break;case"resume":case"record":case"menu":case"info":itemsContainer=(target=e.target).closest(".itemsContainer"),(itemElement=getItemElementFromChildNode(target,null,itemsContainer))&&(e.preventDefault(),e.stopPropagation(),executeAction(e,itemElement,itemsContainer,itemElement,cmd));break;case"pageup":(itemsContainer=(target=e.target).closest(".itemsContainer"))&&(scroller=itemsContainer.closest("[is=emby-scroller]"))&&"false"===scroller.getAttribute("data-horizontal")&&(itemsContainer.pageUp(target),e.preventDefault(),e.stopPropagation());break;case"pagedown":(itemsContainer=(target=e.target).closest(".itemsContainer"))&&(scroller=itemsContainer.closest("[is=emby-scroller]"))&&"false"===scroller.getAttribute("data-horizontal")&&(itemsContainer.pageDown(target),e.preventDefault(),e.stopPropagation());break;case"end":(itemsContainer=(target=e.target).closest(".itemsContainer"))&&(scroller=itemsContainer.closest("[is=emby-scroller]"))&&"false"===scroller.getAttribute("data-horizontal")&&(itemsContainer.focusLast(),e.preventDefault(),e.stopPropagation());break;case"select":"DIV"===(target=e.target).tagName&&!target.closest("a,button,.itemAction")&&(itemsContainer=target.closest(".itemsContainer"),itemElement=getItemElementFromChildNode(target,null,itemsContainer))&&((itemElement.querySelector(".itemAction")||itemElement).click(),e.preventDefault(),e.stopPropagation())}}Object.defineProperty(_exports,"__esModule",{value:!0}),_exports.default=void 0;_exports.default={on:function(context,options){!1!==(options=options||{}).click&&context.addEventListener("click",onClick),!1!==options.command&&_inputmanager.default.on(context,onCommand)},off:function(context,options){options=options||{},context.removeEventListener("click",onClick),!1!==options.command&&_inputmanager.default.off(context,onCommand)},onClick:onClick,getShortcutAttributesHtml:function(item,options){var type,dataAttributes="";return!options.isBoundListItem&&((type=item.Type)&&(dataAttributes+=' data-type="'+type+'"'),(type=item.ServerId||options.serverId)&&(dataAttributes+=' data-serverid="'+type+'"'),(type=item.MediaType)&&(dataAttributes+=' data-mediatype="'+type+'"'),type=item.ChannelId)&&(dataAttributes+=' data-channelid="'+type+'"'),options.isVirtualList||(type=item.Id||item.ItemId)&&(dataAttributes+=' data-id="'+type+'"'),dataAttributes},getShortcutAttributes:function(item,options){var type,dataAttributes=[];return!options.isBoundListItem&&((type=item.Type)&&dataAttributes.push({name:"data-type",value:type}),(type=item.ServerId||options.serverId)&&dataAttributes.push({name:"data-serverid",value:type}),(type=item.MediaType)&&dataAttributes.push({name:"data-mediatype",value:type}),type=item.ChannelId)&&dataAttributes.push({name:"data-channelid",value:type}),options.isVirtualList||(type=item.Id||item.ItemId)&&dataAttributes.push({name:"data-id",value:type}),dataAttributes},getItemElementFromChildNode:getItemElementFromChildNode,getItemFromChildNode:function(child,isMainElement,itemsContainer){return(child=getItemElementFromChildNode(child,isMainElement,itemsContainer))?getItemFromElement(child,itemsContainer):null},getItemFromElement:getItemFromElement}});
/* D3:oss-shortcut-command-source */

const strmAssistantCommandSource = {
    getCommands: function(options) {
        const locale = this.globalize.getCurrentLocale().toLowerCase();

        /* D94:clear-names-start */
        const clearMediaInfoCommandName = locale === 'zh-cn' ? '\u6E05\u9664\u5A92\u4F53\u4FE1\u606F' :
            (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u5A92\u9AD4\u8CC7\u8A0A' : 'Clear MediaInfo');
        const clearChapterImageCommandName = locale === 'zh-cn' ? '\u6E05\u9664\u7AE0\u8282\u56FE' :
            (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7AE0\u7BC0\u5716' : 'Clear Chapter Images');
        /* D94:clear-names-end */
        const cjk = ['zh', 'ja', 'ko'].some(lang => locale.startsWith(lang));
        const lockCommandName = ({
            'zh-cn': '\u9501\u5B9A',
            'zh-hk': '\u9396\u5B9A',
            'zh-tw': '\u9396\u5B9A'
        }[locale] || 'Lock') + (cjk ? this.globalize.translate('Metadata') : ' ' + this.globalize.translate('Metadata'));
        const unlockCommandName = ({
            'zh-cn': '\u89E3\u9501',
            'zh-hk': '\u89E3\u9396',
            'zh-tw': '\u89E3\u9396'
        }[locale] || 'Unlock') + (cjk ? this.globalize.translate('Metadata') : ' ' + this.globalize.translate('Metadata'));

        if (options.items?.length === 1 && options.items[0].LibraryOptions && options.items[0].Type === 'VirtualFolder' &&
            options.items[0].CollectionType !== 'boxsets' && options.items[0].CollectionType !== 'playlists') {
            const commandName = (locale === 'zh-cn') ? '\u590D\u5236' : (['zh-hk', 'zh-tw'].includes(locale) ? '\u8907\u8F38' : 'Copy');
            return [{ name: commandName, id: 'copy', icon: 'content_copy' }];
        }
        if (options.items?.length === 1 && options.items[0].LibraryOptions && options.items[0].Type === 'VirtualFolder' &&
            options.items[0].CollectionType === 'boxsets') {
            return [{ name: this.globalize.translate('Remove'), id: 'remove', icon: 'remove_circle_outline' }];
        }
        if (options.items?.length === 1) {
            const result = [];
            /* D93:delperson-menu */
            if (options.items[0].Type === 'Person' && options.user?.Policy?.IsAdministrator) {
                result.push({ name: this.globalize.translate('Delete'), id: 'delperson', icon: 'delete' });
            }
            if (options.items[0].Type === 'Movie') {
                result.push({ name: this.globalize.translate('HeaderScanLibraryFiles'), id: 'traverse', icon: 'refresh' });
            }
            if ((options.items[0].Type === 'Movie' || options.items[0].Type === 'Episode') &&
                 options.items[0].CanDelete && options.mediaSourceId && options.items[0].MediaSources.length > 1) {
                result.push({
                    name: cjk
                        ? this.globalize.translate('Delete') + this.globalize.translate('Version')
                        : this.globalize.translate('Delete') + ' ' + this.globalize.translate('Version'),
                    id: 'delver_' + options.mediaSourceId,
                    icon: 'remove'
                });
            }
            if (options.items[0].hasOwnProperty('LockData') && options.items[0].Type !== 'CollectionFolder' &&
                (options.user && options.user?.Policy?.IsAdministrator || false)) {
                if (options.items[0].LockData) {
                    result.push({ name: unlockCommandName, id: 'unlock', icon: 'lock_open' });
                } else {
                    result.push({ name: lockCommandName, id: 'lock', icon: 'lock' });
                }
            }
            if ((options.items[0].Type === 'Series' || options.items[0].Type === 'Season') &&
                (options.user && options.user?.Policy?.IsAdministrator || false)) {
                const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7247\u5934\u6807\u8BB0' : 
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7247\u982D\u6A19\u8A18' : 'Clear Intro Markers');
                result.push({ name: commandName, id: 'clear_intro', icon: 'clear_all' });
            }
            /* D94:clear-single-start */
            if (options.user?.Policy?.IsAdministrator) {
            /* D94:clear-menu-start */
            if (options.items[0].MediaType === 'Video' || options.items[0].MediaType === 'Audio' ||
                options.items[0].Type === 'Series' || options.items[0].Type === 'Season' || options.items[0].Type === 'MusicAlbum') {
                result.push({ name: clearMediaInfoCommandName, id: 'clear_mediainfo', icon: 'cleaning_services' });
            }
            if (options.items[0].MediaType === 'Video' || options.items[0].Type === 'Series' || options.items[0].Type === 'Season') {
                result.push({ name: clearChapterImageCommandName, id: 'clear_chapter_image', icon: 'hide_image' });
            }
            /* D94:clear-menu-end */
            }
            /* D94:clear-single-end */
            return result;
        }
        if (!options.multiSelect && options.items?.length > 1 &&
            ((options.users && Object.values(options.users)[0]?.Policy?.IsAdministrator) || false)) {
            const result = [];
            /* D94:collection-lock-start */
            if (options.items[0].Type !== 'CollectionFolder') {
            result.push({ name: lockCommandName, id: 'lock', icon: 'lock' });
            result.push({ name: unlockCommandName, id: 'unlock', icon: 'lock_open' });
            }
            /* D94:collection-lock-end */
            /* D94:clear-menu-start */
            if (options.items[0].MediaType === 'Video' || options.items[0].MediaType === 'Audio' ||
                options.items[0].Type === 'Series' || options.items[0].Type === 'Season' || options.items[0].Type === 'MusicAlbum') {
                result.push({ name: clearMediaInfoCommandName, id: 'clear_mediainfo', icon: 'cleaning_services' });
            }
            if (options.items[0].MediaType === 'Video' || options.items[0].Type === 'Series' || options.items[0].Type === 'Season') {
                result.push({ name: clearChapterImageCommandName, id: 'clear_chapter_image', icon: 'hide_image' });
            }
            /* D94:clear-menu-end */
            return result;
        }
        return [];
    },
    executeCommand: function(command, items) {
        if (!command || !items?.length) return;
        const actions = {
            copy: 'copy',
            remove: 'remove',
            traverse: 'traverse',
            delperson: 'delperson',
            lock: 'lock',
            unlock: 'unlock',
            clear_intro: 'clear_intro'
        };
        /* D94:clear-dispatch-start */
        if (command === 'clear_mediainfo' || command === 'clear_chapter_image') {
            const itemIds = items.map(item => item?.Id);
            if (itemIds.some(id => typeof id !== 'string' || !id.trim())) return Promise.reject(new Error('An item Id is required.'));
            return require(['components/strmassistant/strmassistant']).then(responses => {
                const locale = this.globalize.getCurrentLocale().toLowerCase();
                const commandName = command === 'clear_mediainfo'
                    ? (locale === 'zh-cn' ? '\u6E05\u9664\u5A92\u4F53\u4FE1\u606F' : ['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u5A92\u9AD4\u8CC7\u8A0A' : 'Clear MediaInfo')
                    : (locale === 'zh-cn' ? '\u6E05\u9664\u7AE0\u8282\u56FE' : ['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7AE0\u7BC0\u5716' : 'Clear Chapter Images');
                return this.confirm({
                    text: this.globalize.translate('AreYouSureToContinue'),
                    title: commandName,
                    confirmText: this.globalize.translate('Clear'),
                    primary: 'cancel'
                }).then(() => Promise.allSettled(itemIds.map(id => Promise.resolve().then(() => responses[0][command](id))))).then(results => {
                    const failed = results.find(result => result.status === 'rejected');
                    if (failed) throw Object.assign(new Error(failed.reason?.batchResult ? failed.reason.message : this.globalize.translate('Error')), {
                        cause: failed.reason,
                        itemResults: results.map((result, index) => Object.assign({ Id: itemIds[index] }, result))
                    });
                    this.toast(commandName + (['zh-cn', 'zh-hk', 'zh-tw'].includes(locale) ? '\u6210\u529F' : ' Success'));
                    return results.map(result => result.value);
                });
            });
        }
        /* D94:clear-dispatch-end */
        if (command.startsWith('delver_')) {
            const mediaSourceId = command.replace('delver_', '');
            const mediaSources = items[0].MediaSources || [];
            const matchingItem = mediaSources.find(source => source.Id === mediaSourceId);
            const itemId = matchingItem?.ItemId;
            const itemName = matchingItem?.Name;
            if (itemId && itemName) {
                return require(['components/strmassistant/strmassistant']).then(responses => {
                    return responses[0].delver(itemId, itemName, items[0].Type);
                });
            }
        }
        if (command === actions.lock || command === actions.unlock) {
            const lockData = command === actions.lock;
            return require(['components/strmassistant/strmassistant']).then(responses => {
                const promises = items.map(item => responses[0].lock(item.Id, lockData));
                return Promise.all(promises);
            });
        }
        if (actions[command]) {
            return require(['components/strmassistant/strmassistant']).then(responses => {
                if (command === 'traverse') {
                    return responses[0][actions[command]](items[0].ParentId);
                }
                return responses[0][actions[command]](items[0].Id, items[0].Name);
            });
        }
    }
};

setTimeout(() => {
    Emby.importModule('./modules/common/globalize.js').then(globalize => {
        strmAssistantCommandSource.globalize = globalize;
        /* D94:clear-imports-start */
        return Promise.all([
            Emby.importModule('./modules/common/dialogs/confirm.js'),
            Emby.importModule('./modules/toast/toast.js')
        ]).then(([confirm, toast]) => {
        strmAssistantCommandSource.confirm = confirm;
        strmAssistantCommandSource.toast = toast;
        /* D94:clear-imports-end */
        Emby.importModule('./modules/common/itemmanager/itemmanager.js').then(itemmanager => {
            itemmanager.registerCommandSource(strmAssistantCommandSource);
        });
        /* D94:clear-imports-close */
        });
    });
}, 3000);
    