(function(global, $) {
  global.insiteScripts = global.insiteScripts || {};
  global.insiteScripts.message = global.insiteScripts.message || function message(scriptOptions) {
      var dmSmartScriptDuration = scriptOptions.duration,
          dmSmartScriptSettings = scriptOptions.settings,
          dmSmartScriptRuleId = scriptOptions.ruleId,
          dmSmartScriptDontParseSettings = scriptOptions.dontParseSettings,
          dmSmartScriptDontSendCloseEvent = scriptOptions.dontSendCloseEvent;

      // don't show a notification if another notification is currently displaying
      var currentNotification = document.querySelector('#d-notification-bar');
      if (currentNotification && !currentNotification.getAttribute('data-was-shown')) {
          return;
      }
      var settings = dmSmartScriptDontParseSettings ? dmSmartScriptSettings : JSON.parse(atob(dmSmartScriptSettings));
      var messageBody = decodeURIComponent(settings.body);

      if (scriptOptions.useNew && 'runtime' in window) {
          $(global.document).ready(function () {
              var layoutContainer = document.querySelector('[dmtemplateid]');
              return global.runtime.notify({
                  markup: messageBody,
                  delay: settings.delay || 1,
                  messageContainer: layoutContainer,
                  background: settings.background,
                  duration: scriptOptions.duration || -1,
                  shouldMoveContainer: !/amburger/i.test(layoutContainer.getAttribute('dmtemplateid'))
              });
          });
          return;
      }
      var messageDiv = $('<div id="d-notification-bar">'),
          closeTrigger = $('<div>&times;</div>');

      function invert(rgb) {
          if (!/rgba/i.test(rgb)) {
              return '#fff';
          }
          var rgbArr = [].slice.call(arguments).join(',').replace(/[rgba\(\)\s]/gi, '').split(',');
          var rgbStr = rgbArr.map(function (colorCmpt, idx) {
              if (idx === 3) {
                  return 1;
              }
              return 255 - colorCmpt;
          }).join(', ');
          return 'rgba(' + rgbStr + ')';
      }

      function toggleMessage(open) {
          var bodyEl = document.body;

          /*******************************************************************************************/
          /* Before attempting to implement a better solution                                        */
          /* (like using only `transform` because it's the only property                             */
          /* beside opacity that doesn't require re-paints)                                          */
          /* please note that there's an issue with `position: fixed` and `transform` on the parent. */
          /* You can read about it here: https://www.w3.org/TR/css-transforms-1/#transform-rendering */
          /*******************************************************************************************/

          // Make sure body has transition - if not add appropriate class
          if (!bodyEl.classList.contains('showing-message')) {
              bodyEl.classList.add('showing-message');
          }

          if (open) {
              bodyEl.style.top = messageDiv.outerHeight() + 'px';
              messageDiv[0].style.transform = 'translateY(0)';
          } else {
              bodyEl.style.top = '0';
              messageDiv[0].style.transform = '';
              messageDiv.attr('data-was-shown', 'true');
          }
          messageDiv[0].dataset.visible = '' + open;
      }

      messageDiv.attr('data-rule', dmSmartScriptRuleId).attr('data-rule-type', 'notification');
      messageDiv.html(messageBody);
      messageDiv.css({
          background: settings.background || 'rgba(0, 0, 0, 0.8)',
          color: settings.background ? invert(settings.background) : '#fff'
      });
      messageDiv.find('a').css({
        color: 'inherit'
      }).on('click', function() {
    	  if(!dmSmartScriptDontSendCloseEvent) {
    		  dm_gaq_push_event('notificationLinkClick', null, null, Parameters.SiteAlias, this);
    	  }
        toggleMessage(false);
      });
      closeTrigger.css({
        position: 'absolute',
        top: '5px',
        right: '10px',
        'font-weight': 'bold',
        cursor: 'pointer',
        color: settings.background ? invert(settings.background) : '#fff'
      }).on('click', function() {
          if(!dmSmartScriptDontSendCloseEvent) {
              dm_gaq_push_event('notificationClose', null, null, Parameters.SiteAlias, this);
          }
          toggleMessage(false);
      }).appendTo(messageDiv);

      var triggerMessage = function() {
          messageDiv.prependTo($('body'));
          const urls = $(messageDiv).find('a');
          $.DM.initAjaxLinks(urls);
          var delay = settings.delay ? settings.delay * 1000 : 2000;
          setTimeout(function () {
            toggleMessage(true);
          }, delay);
          if (dmSmartScriptDuration) {
            setTimeout(function() {
              toggleMessage(false);
            }, delay + dmSmartScriptDuration * 1000);
          }
      }
      if ($.isReady) {
        triggerMessage();
      } else {
        $(document).ready(function () {
          triggerMessage();
        });
      }
  };
})(this, jQuery);
