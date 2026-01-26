/******/ (function(modules) { // webpackBootstrap
/******/ 	// The module cache
/******/ 	var installedModules = {};

/******/ 	// The require function
/******/ 	function __webpack_require__(moduleId) {

/******/ 		// Check if module is in cache
/******/ 		if(installedModules[moduleId])
/******/ 			return installedModules[moduleId].exports;

/******/ 		// Create a new module (and put it into the cache)
/******/ 		var module = installedModules[moduleId] = {
/******/ 			exports: {},
/******/ 			id: moduleId,
/******/ 			loaded: false
/******/ 		};

/******/ 		// Execute the module function
/******/ 		modules[moduleId].call(module.exports, module, module.exports, __webpack_require__);

/******/ 		// Flag the module as loaded
/******/ 		module.loaded = true;

/******/ 		// Return the exports of the module
/******/ 		return module.exports;
/******/ 	}


/******/ 	// expose the modules object (__webpack_modules__)
/******/ 	__webpack_require__.m = modules;

/******/ 	// expose the module cache
/******/ 	__webpack_require__.c = installedModules;

/******/ 	// __webpack_public_path__
/******/ 	__webpack_require__.p = "";

/******/ 	// Load entry module and return exports
/******/ 	return __webpack_require__(0);
/******/ })
/************************************************************************/
/******/ ([
/* 0 */
/***/ function(module, exports, __webpack_require__) {

	"use strict";

	function _interopRequireDefault(obj) { return obj && obj.__esModule ? obj : { "default": obj }; }

	function _defineProperty(obj, key, value) { if (key in obj) { Object.defineProperty(obj, key, { value: value, enumerable: true, configurable: true, writable: true }); } else { obj[key] = value; } return obj; }

	var _commonConstants = __webpack_require__(10);

	var _commonUtils = __webpack_require__(13);

	var _commonCrossFrame = __webpack_require__(2);

	var _commonCrossFrame2 = _interopRequireDefault(_commonCrossFrame);

	var crosserInstance;
	var myDomain = location.origin;
	var frameId = "notifs_frame";

	var frameDomain = null;
	var basePath = null;
	var frameInstance = null;
	var siteAlias = null;
	var initialPushSupportIndicator = null;
	var currentNotificationState = _commonConstants.State.UNSUPPORTED;
	var handlers = {};
	var defaultHandlers = {
	  stateChanged: console.log.bind(console),
	  addSubscription: console.log.bind(console),
	  removeSubscription: console.log.bind(console)
	};

	/**
	 * initializes the subscription handler. will run only once
	 * @param  {string} siteFrameDomain the domain of the iframe
	 * @param  {object} initialHandlers the handlers that should handle with the status changes
	 * @return {[type]}                         [description]
	 */

	function init() {
	  var _ref = arguments.length <= 0 || arguments[0] === undefined ? {} : arguments[0];

	  var sslFrameDomain = _ref.sslFrameDomain;
	  var sslPublicPath = _ref.sslPublicPath;
	  var runtimeSiteAlias = _ref.runtimeSiteAlias;
	  var initialPushSupport = _ref.initialPushSupport;
	  var _ref$initialHandlers = _ref.initialHandlers;
	  var initialHandlers = _ref$initialHandlers === undefined ? defaultHandlers : _ref$initialHandlers;

	  // init the connecting frame if needed
	  if (frameInstance == null) {
	    if (frameDomain == null) {
	      frameDomain = sslFrameDomain;
	    }
	    if (basePath == null) {
	      basePath = sslPublicPath;
	    }
	    if (siteAlias == null) {
	      siteAlias = runtimeSiteAlias;
	    }
	    if (initialPushSupportIndicator == null) {
	      initialPushSupportIndicator = initialPushSupport;
	    }
	    handlers = initialHandlers;
	    frameInstance = createFrame(initCrosser);
	    document.body.appendChild(frameInstance);
	  }
	  // in case the frame is already intialized
	  else {
	    sendState();
	  }

	  return true;
	}

	/**
	 * registers a button to be a trigger for subscription
	 * @param  {DOMElement} triggerElement the button to register
	 */
	function setAsTriggerElement(triggerElement) {
	  if (triggerElement) {
	    // remove event handlers
	    var triggerElementClone = triggerElement.cloneNode(true);
	    triggerElement.parentNode.replaceChild(triggerElementClone, triggerElement);
	    triggerElement = triggerElementClone;
	    // triggerElement.removeEventListener("click mousedown touchstart touchend touchmove");

	    var params = {};
	    triggerElement.href = (0, _commonUtils.buildUrl)(basePath + _commonConstants.Endpoints.POPUP, params);
	    triggerElement.target = "_blank";
	    triggerElement.addEventListener("click", function (e) {
	      // don't let it fire if notifications are blocked/enabled/unsupported
	      if (currentNotificationState != _commonConstants.State.DISABLED) {
	        e.preventDefault();
	      }
	      // fire unsubscribe if notifications are enabled
	      if (currentNotificationState == _commonConstants.State.ENABLED) {
	        crosserInstance.triggerEvent(_commonConstants.Event.UNSUBSCRIBE, {});
	      }
	    });
	  }
	}

	function sendState() {
	  fireSafe(handlers.stateChanged, currentNotificationState);
	}

	/**
	 * creates the connecting https frame that will handle the subscriptions
	 * @return {DOMElement} the created frame
	 */
	function createFrame(onLoad) {
	  var f = document.createElement("iframe");
	  f.style.display = "none";
	  f.id = frameId;
	  f.onload = onLoad;
	  f.src = (0, _commonUtils.buildUrl)(basePath + _commonConstants.Endpoints.FRAME, _defineProperty({}, _commonConstants.Consts.PARENT_ORIGIN_PARAM, myDomain));
	  return f;
	}

	/**
	 * initializes the crosser after the frame loads, 
	 * listens to events, and triggers the "init" event in the frame
	 */
	function initCrosser() {
	  var _crosserInstance$triggerEvent;

	  crosserInstance = new _commonCrossFrame2["default"](frameInstance.contentWindow, frameDomain);
	  listenToEvents();

	  crosserInstance.triggerEvent(_commonConstants.Event.INIT, (_crosserInstance$triggerEvent = {}, _defineProperty(_crosserInstance$triggerEvent, _commonConstants.Consts.SITE_DOMAIN_PARAM, myDomain), _defineProperty(_crosserInstance$triggerEvent, _commonConstants.Consts.INITIAL_PUSH_SUPPORT_PARAM, initialPushSupportIndicator), _defineProperty(_crosserInstance$triggerEvent, _commonConstants.Consts.SITE_ALIAS_PARAM, siteAlias), _crosserInstance$triggerEvent));
	}

	/**
	 * handles events that are being sent from the frame (notification status change, server orders, etc.)
	 */
	function listenToEvents() {
	  crosserInstance.subscribeEvent(_commonConstants.Event.CHANGE_NOTIFICATION_STATUS, function (data) {
	    currentNotificationState = data.state;
	    fireSafe(handlers.stateChanged, data.state);
	  });
	  crosserInstance.subscribeEvent(_commonConstants.Event.ADD_SUBSCRIPTION, function (data) {
	    fireSafe(handlers.addSubscription, data);
	  });
	  crosserInstance.subscribeEvent(_commonConstants.Event.REMOVE_SUBSCRIPTION, function (data) {
	    fireSafe(handlers.removeSubscription, data);
	  });
	}

	function fireSafe(fn) {
	  for (var _len = arguments.length, args = Array(_len > 1 ? _len - 1 : 0), _key = 1; _key < _len; _key++) {
	    args[_key - 1] = arguments[_key];
	  }

	  return (fn || function () {}).apply(undefined, args);
	}

	window.pushService = Object.defineProperties({
	  init: init,
	  sendState: sendState,
	  setAsTriggerElement: setAsTriggerElement
	}, {
	  state: {
	    get: function get() {
	      return currentNotificationState;
	    },
	    configurable: true,
	    enumerable: true
	  },
	  handlers: {
	    set: function set() {
	      var newHandlers = arguments.length <= 0 || arguments[0] === undefined ? {} : arguments[0];

	      handlers = newHandlers;
	    },
	    configurable: true,
	    enumerable: true
	  }
	});

	// [Consts.SITE_DOMAIN_PARAM]: myDomain,
	// [Consts.SITE_ALIAS_PARAM]: siteAlias

/***/ },
/* 1 */,
/* 2 */
/***/ function(module, exports, __webpack_require__) {

	'use strict';

	var Promise = __webpack_require__(3);

	var generateId = function generateId() {
	  return new Date().getTime().toString();
	};

	function Crosser(otherFrameWindow, origin) {
	  /* private */
	  this._sessionHandlers = {};
	  this._listeners = {};
	  this._otherFrameWindow = otherFrameWindow;
	  this._otherOrigin = origin || '*';
	  this._id = generateId();
	  this._events = {};

	  if (!this._otherFrameWindow || !this._otherFrameWindow.postMessage) {
	    throw new Error('Missing frame to communicate with');
	  }

	  window.addEventListener('message', this._receiveMessage.bind(this), false);
	};

	Crosser.prototype._receiveMessage = function (event) {
	  var message = event.data,
	      doesOriginMatch = this._doesOriginMatch(event.origin);

	  if (!doesOriginMatch || !message) {
	    return;
	  }

	  if (message.type === 'event') {
	    if (message.eventName && this._events[message.eventName]) {
	      this._fireEvent(event);
	    }
	  } else {
	    if (message.sessionName && this._sessionHandlers[message.sessionName] && message.creator === this._id) {

	      this._endSession(event);
	    } else if (message.sessionName && message.creator !== this._id) {

	      this._throwBackSession(event);
	    }
	  }
	};

	Crosser.prototype._doesOriginMatch = function (eventOrigin) {
	  var ret = eventOrigin === this._otherOrigin || this._otherOrigin === '*' && window.location.origin === eventOrigin;
	  return ret;
	};

	Crosser.prototype._endSession = function (event) {
	  var message = event.data;
	  var resolve = this._sessionHandlers[message.sessionName].resolve;
	  var reject = this._sessionHandlers[message.sessionName].reject;

	  this._deleteSession(message.sessionName);

	  if (message.error) {
	    reject(message.error);
	  } else {
	    resolve(message.payload);
	  }
	};

	Crosser.prototype._throwBackSession = function (event) {
	  var message = event.data,
	      sessionName = message.sessionName;

	  Object.keys(this._listeners[sessionName] || {}).forEach(function (subscriberId) {
	    var callbackResult = this._listeners[sessionName][subscriberId](message.payload);
	    if (callbackResult && callbackResult.then) {
	      callbackResult.then((function (resolvedPayload) {
	        this._postMessage({
	          sessionName: sessionName,
	          payload: resolvedPayload,
	          creator: message.creator
	        });
	      }).bind(this));
	    } else {
	      this._postMessage({
	        sessionName: sessionName,
	        payload: callbackResult,
	        creator: message.creator
	      });
	    }
	  }, this);
	};

	Crosser.prototype._postMessage = function (message) {
	  this._otherFrameWindow.postMessage(message, this._otherOrigin);
	};

	Crosser.prototype._deleteSession = function (sessionName) {
	  this._sessionHandlers[sessionName].resolve = null;
	  delete this._sessionHandlers[sessionName].resolve;
	  this._sessionHandlers[sessionName].reject = null;
	  delete this._sessionHandlers[sessionName].reject;
	  this._sessionHandlers[sessionName] = null;
	  delete this._sessionHandlers[sessionName];
	};

	Crosser.prototype.destroy = function () {
	  Object.keys(this._listeners || {}).forEach(this.unsubscribe, this);
	  Object.keys(this._sessionHandlers || {}).forEach(this._deleteSession, this);
	  this._otherFrameWindow = null;
	  this._otherOrigin = null;
	  this._id = null;
	};

	Crosser.prototype.trigger = function (sessionName, payload) {
	  var promise;

	  if (this._sessionHandlers[sessionName]) {
	    throw new Error('A session with the name ' + sessionName + ' is still alive');
	  }

	  promise = new Promise((function (resolve, reject) {
	    this._sessionHandlers[sessionName] = {
	      resolve: resolve,
	      reject: reject
	    };
	  }).bind(this));

	  this._postMessage({
	    sessionName: sessionName,
	    payload: payload,
	    creator: this._id
	  });

	  return promise;
	};

	Crosser.prototype.abort = function (sessionName) {
	  this._deleteSession(sessionName);
	};

	Crosser.prototype.subscribe = function (sessionName, callback) {
	  var subscriberId = generateId();

	  this._listeners[sessionName] = this._listeners[sessionName] || {};
	  if (Object.keys(this._listeners[sessionName] || {}).length > 0) {
	    throw new Error('A session ( ' + sessionName + ' ) can have only one subscriber');
	  }

	  this._listeners[sessionName][subscriberId] = callback;
	  return subscriberId;
	};

	Crosser.prototype.unsubscribe = function (sessionName, subscriberId) {
	  if (!subscriberId) {
	    Object.keys(this._listeners[sessionName] || {}).forEach(this.unsubscribe.bind(this, sessionName));
	  } else {
	    this._listeners[sessionName][subscriberId] = null;
	    delete this._listeners[sessionName][subscriberId];
	  }
	};

	Crosser.prototype._fireEvent = function (event) {
	  var message = event.data;

	  this._events[message.eventName].forEach(function (callback) {
	    callback(message.payload);
	  });
	};

	Crosser.prototype.subscribeEvent = function (eventName, callback) {
	  this._events[eventName] = this._events[eventName] || [];
	  this._events[eventName].push(callback);
	};

	Crosser.prototype.unsubscribeEvent = function (eventName) {
	  this._events[eventName] = null;
	  delete this._events[eventName];
	};

	Crosser.prototype.triggerEvent = function (eventName, payload) {
	  this._postMessage({
	    eventName: eventName,
	    type: 'event',
	    payload: payload,
	    creator: this._id
	  });
	};

	module.exports = Crosser;

/***/ },
/* 3 */
/***/ function(module, exports, __webpack_require__) {

	var __WEBPACK_AMD_DEFINE_RESULT__;/* WEBPACK VAR INJECTION */(function(global, setImmediate) {/*! Native Promise Only
	    v0.8.0-a (c) Kyle Simpson
	    MIT License: http://getify.mit-license.org
	*/
	!function(t,n,e){n[t]=n[t]||e(),"undefined"!=typeof module&&module.exports?module.exports=n[t]:"function"=="function"&&__webpack_require__(6)&&!(__WEBPACK_AMD_DEFINE_RESULT__ = function(){return n[t]}.call(exports, __webpack_require__, exports, module), __WEBPACK_AMD_DEFINE_RESULT__ !== undefined && (module.exports = __WEBPACK_AMD_DEFINE_RESULT__))}("Promise","undefined"!=typeof global?global:this,function(){"use strict";function t(t,n){l.add(t,n),h||(h=y(l.drain))}function n(t){var n,e=typeof t;return null==t||"object"!=e&&"function"!=e||(n=t.then),"function"==typeof n?n:!1}function e(){for(var t=0;t<this.chain.length;t++)o(this,1===this.state?this.chain[t].success:this.chain[t].failure,this.chain[t]);this.chain.length=0}function o(t,e,o){var r,i;try{e===!1?o.reject(t.msg):(r=e===!0?t.msg:e.call(void 0,t.msg),r===o.promise?o.reject(TypeError("Promise-chain cycle")):(i=n(r))?i.call(r,o.resolve,o.reject):o.resolve(r))}catch(c){o.reject(c)}}function r(o){var c,u=this;if(!u.triggered){u.triggered=!0,u.def&&(u=u.def);try{(c=n(o))?t(function(){var t=new f(u);try{c.call(o,function(){r.apply(t,arguments)},function(){i.apply(t,arguments)})}catch(n){i.call(t,n)}}):(u.msg=o,u.state=1,u.chain.length>0&&t(e,u))}catch(a){i.call(new f(u),a)}}}function i(n){var o=this;o.triggered||(o.triggered=!0,o.def&&(o=o.def),o.msg=n,o.state=2,o.chain.length>0&&t(e,o))}function c(t,n,e,o){for(var r=0;r<n.length;r++)!function(r){t.resolve(n[r]).then(function(t){e(r,t)},o)}(r)}function f(t){this.def=t,this.triggered=!1}function u(t){this.promise=t,this.state=0,this.triggered=!1,this.chain=[],this.msg=void 0}function a(n){if("function"!=typeof n)throw TypeError("Not a function");if(0!==this.__NPO__)throw TypeError("Not a promise");this.__NPO__=1;var o=new u(this);this.then=function(n,r){var i={success:"function"==typeof n?n:!0,failure:"function"==typeof r?r:!1};return i.promise=new this.constructor(function(t,n){if("function"!=typeof t||"function"!=typeof n)throw TypeError("Not a function");i.resolve=t,i.reject=n}),o.chain.push(i),0!==o.state&&t(e,o),i.promise},this["catch"]=function(t){return this.then(void 0,t)};try{n.call(void 0,function(t){r.call(o,t)},function(t){i.call(o,t)})}catch(c){i.call(o,c)}}var s,h,l,p=Object.prototype.toString,y="undefined"!=typeof setImmediate?function(t){return setImmediate(t)}:setTimeout;try{Object.defineProperty({},"x",{}),s=function(t,n,e,o){return Object.defineProperty(t,n,{value:e,writable:!0,configurable:o!==!1})}}catch(d){s=function(t,n,e){return t[n]=e,t}}l=function(){function t(t,n){this.fn=t,this.self=n,this.next=void 0}var n,e,o;return{add:function(r,i){o=new t(r,i),e?e.next=o:n=o,e=o,o=void 0},drain:function(){var t=n;for(n=e=h=void 0;t;)t.fn.call(t.self),t=t.next}}}();var g=s({},"constructor",a,!1);return a.prototype=g,s(g,"__NPO__",0,!1),s(a,"resolve",function(t){var n=this;return t&&"object"==typeof t&&1===t.__NPO__?t:new n(function(n,e){if("function"!=typeof n||"function"!=typeof e)throw TypeError("Not a function");n(t)})}),s(a,"reject",function(t){return new this(function(n,e){if("function"!=typeof n||"function"!=typeof e)throw TypeError("Not a function");e(t)})}),s(a,"all",function(t){var n=this;return"[object Array]"!=p.call(t)?n.reject(TypeError("Not an array")):0===t.length?n.resolve([]):new n(function(e,o){if("function"!=typeof e||"function"!=typeof o)throw TypeError("Not a function");var r=t.length,i=Array(r),f=0;c(n,t,function(t,n){i[t]=n,++f===r&&e(i)},o)})}),s(a,"race",function(t){var n=this;return"[object Array]"!=p.call(t)?n.reject(TypeError("Not an array")):new n(function(e,o){if("function"!=typeof e||"function"!=typeof o)throw TypeError("Not a function");c(n,t,function(t,n){e(n)},o)})}),a});

	/* WEBPACK VAR INJECTION */}.call(exports, (function() { return this; }()), __webpack_require__(4).setImmediate))

/***/ },
/* 4 */
/***/ function(module, exports, __webpack_require__) {

	/* WEBPACK VAR INJECTION */(function(setImmediate, clearImmediate) {var nextTick = __webpack_require__(5).nextTick;
	var apply = Function.prototype.apply;
	var slice = Array.prototype.slice;
	var immediateIds = {};
	var nextImmediateId = 0;

	// DOM APIs, for completeness

	exports.setTimeout = function() {
	  return new Timeout(apply.call(setTimeout, window, arguments), clearTimeout);
	};
	exports.setInterval = function() {
	  return new Timeout(apply.call(setInterval, window, arguments), clearInterval);
	};
	exports.clearTimeout =
	exports.clearInterval = function(timeout) { timeout.close(); };

	function Timeout(id, clearFn) {
	  this._id = id;
	  this._clearFn = clearFn;
	}
	Timeout.prototype.unref = Timeout.prototype.ref = function() {};
	Timeout.prototype.close = function() {
	  this._clearFn.call(window, this._id);
	};

	// Does not start the time, just sets up the members needed.
	exports.enroll = function(item, msecs) {
	  clearTimeout(item._idleTimeoutId);
	  item._idleTimeout = msecs;
	};

	exports.unenroll = function(item) {
	  clearTimeout(item._idleTimeoutId);
	  item._idleTimeout = -1;
	};

	exports._unrefActive = exports.active = function(item) {
	  clearTimeout(item._idleTimeoutId);

	  var msecs = item._idleTimeout;
	  if (msecs >= 0) {
	    item._idleTimeoutId = setTimeout(function onTimeout() {
	      if (item._onTimeout)
	        item._onTimeout();
	    }, msecs);
	  }
	};

	// That's not how node.js implements it but the exposed api is the same.
	exports.setImmediate = typeof setImmediate === "function" ? setImmediate : function(fn) {
	  var id = nextImmediateId++;
	  var args = arguments.length < 2 ? false : slice.call(arguments, 1);

	  immediateIds[id] = true;

	  nextTick(function onNextTick() {
	    if (immediateIds[id]) {
	      // fn.call() is faster so we optimize for the common use-case
	      // @see http://jsperf.com/call-apply-segu
	      if (args) {
	        fn.apply(null, args);
	      } else {
	        fn.call(null);
	      }
	      // Prevent ids from leaking
	      exports.clearImmediate(id);
	    }
	  });

	  return id;
	};

	exports.clearImmediate = typeof clearImmediate === "function" ? clearImmediate : function(id) {
	  delete immediateIds[id];
	};
	/* WEBPACK VAR INJECTION */}.call(exports, __webpack_require__(4).setImmediate, __webpack_require__(4).clearImmediate))

/***/ },
/* 5 */
/***/ function(module, exports) {

	// shim for using process in browser

	var process = module.exports = {};
	var queue = [];
	var draining = false;
	var currentQueue;
	var queueIndex = -1;

	function cleanUpNextTick() {
	    draining = false;
	    if (currentQueue.length) {
	        queue = currentQueue.concat(queue);
	    } else {
	        queueIndex = -1;
	    }
	    if (queue.length) {
	        drainQueue();
	    }
	}

	function drainQueue() {
	    if (draining) {
	        return;
	    }
	    var timeout = setTimeout(cleanUpNextTick);
	    draining = true;

	    var len = queue.length;
	    while(len) {
	        currentQueue = queue;
	        queue = [];
	        while (++queueIndex < len) {
	            currentQueue[queueIndex].run();
	        }
	        queueIndex = -1;
	        len = queue.length;
	    }
	    currentQueue = null;
	    draining = false;
	    clearTimeout(timeout);
	}

	process.nextTick = function (fun) {
	    var args = new Array(arguments.length - 1);
	    if (arguments.length > 1) {
	        for (var i = 1; i < arguments.length; i++) {
	            args[i - 1] = arguments[i];
	        }
	    }
	    queue.push(new Item(fun, args));
	    if (queue.length === 1 && !draining) {
	        setTimeout(drainQueue, 0);
	    }
	};

	// v8 likes predictible objects
	function Item(fun, array) {
	    this.fun = fun;
	    this.array = array;
	}
	Item.prototype.run = function () {
	    this.fun.apply(null, this.array);
	};
	process.title = 'browser';
	process.browser = true;
	process.env = {};
	process.argv = [];
	process.version = ''; // empty string to avoid regexp issues
	process.versions = {};

	function noop() {}

	process.on = noop;
	process.addListener = noop;
	process.once = noop;
	process.off = noop;
	process.removeListener = noop;
	process.removeAllListeners = noop;
	process.emit = noop;

	process.binding = function (name) {
	    throw new Error('process.binding is not supported');
	};

	// TODO(shtylman)
	process.cwd = function () { return '/' };
	process.chdir = function (dir) {
	    throw new Error('process.chdir is not supported');
	};
	process.umask = function() { return 0; };


/***/ },
/* 6 */
/***/ function(module, exports) {

	/* WEBPACK VAR INJECTION */(function(__webpack_amd_options__) {module.exports = __webpack_amd_options__;

	/* WEBPACK VAR INJECTION */}.call(exports, {}))

/***/ },
/* 7 */,
/* 8 */,
/* 9 */,
/* 10 */
/***/ function(module, exports) {

	// event related constants
	"use strict";

	Object.defineProperty(exports, "__esModule", {
	  value: true
	});
	var Event = {
	  POPUP_EVENT: "popup-event",
	  FRAME_EVENT: "frame-event",
	  REQUEST_EVENT: "request-event",
	  RESPONSE_EVENT: "response-event",
	  PARAMS: "params",
	  FETCH: ":fetch",
	  RESPOND: ":respond",
	  ADD_SUBSCRIPTION: "subscription:add",
	  REMOVE_SUBSCRIPTION: "subscription:remove",
	  CHANGE_NOTIFICATION_STATUS: "notification:changed",
	  CHANGE_PERMISSION_REQUEST_STATUS: "permission:changed",
	  INIT: "init",
	  UNSUBSCRIBE: "unsubscribe"
	};

	// notifications state related constants
	var State = {
	  ENABLED: "enabled",
	  DISABLED: "disabled",
	  BLOCKED: "blocked",
	  UNSUPPORTED: "unsupported",

	  REQUEST: "inprogress",
	  GRANTED: "granted"
	};

	// other constants
	var Consts = {
	  PARENT_ORIGIN_PARAM: "parentOrigin",
	  SITE_ALIAS_PARAM: "siteAlias",
	  INITIAL_PUSH_SUPPORT_PARAM: "pushSupport",
	  SITE_DOMAIN_PARAM: "siteDomain"
	};

	// api endpoints
	var Endpoints = {
	  FRAME: "/frame.jsp",
	  POPUP: "/popup.jsp",

	  NOTIFICATIONS: "/_dm/s/rt/api/public/rt/site/{siteAlias}/notifications/lastsent",
	  ICON: "/api/icon",
	  PROXY: "/api/proxy"
	};

	exports["default"] = {
	  Event: Event, State: State, Consts: Consts, Endpoints: Endpoints
	};
	module.exports = exports["default"];

/***/ },
/* 11 */,
/* 12 */,
/* 13 */
/***/ function(module, exports) {

	'use strict';

	Object.defineProperty(exports, '__esModule', {
	  value: true
	});
	exports.getQueryVariableFromUrl = getQueryVariableFromUrl;
	exports.buildUrl = buildUrl;
	exports.serializeSubscription = serializeSubscription;
	exports.fixEndpoint = fixEndpoint;
	exports.getQueryVariable = getQueryVariable;
	exports.getQueryVariables = getQueryVariables;
	exports.validateObject = validateObject;
	exports.storeSubscriptionLocallyAndCompare = storeSubscriptionLocallyAndCompare;
	function getUrlVars(url) {
	  var vars = [],
	      hash;
	  var hashes = url.slice(url.indexOf('?') + 1).split('&');
	  for (var i = 0; i < hashes.length; i++) {
	    hash = hashes[i].split('=');
	    vars.push(hash[0]);
	    vars[hash[0]] = hash[1];
	  }
	  return vars;
	}

	function getQueryVariableFromUrl(variable, url) {
	  return getUrlVars(url)[variable];
	}

	function buildUrl(url, paramsObj) {
	  paramsObj = paramsObj || {};
	  if (!Object.keys(paramsObj).length) {
	    return url;
	  }

	  var resultArray = [];
	  Object.keys(paramsObj).forEach(function (k) {
	    resultArray.push(k + '=' + encodeURIComponent(paramsObj[k]));
	  });
	  var delim = url.indexOf('?') == -1 ? '?' : '&';

	  return url + delim + resultArray.join('&');
	}

	function serializeSubscription(subscription) {
	  return {
	    endpoint: fixEndpoint(subscription)
	  };
	}

	// This method handles the removal of subscriptionId
	// in Chrome 44 by concatenating the subscription Id
	// to the subscription endpoint

	function fixEndpoint(pushSubscription) {
	  // Make sure we only mess with GCM
	  if (pushSubscription.endpoint.indexOf('https://android.googleapis.com/gcm/send') !== 0) {
	    return pushSubscription.endpoint;
	  }

	  var mergedEndpoint = pushSubscription.endpoint;
	  // Chrome 42 + 43 will not have the subscriptionId attached
	  // to the endpoint.
	  if (pushSubscription.subscriptionId && pushSubscription.endpoint.indexOf(pushSubscription.subscriptionId) === -1) {
	    // Handle version 42 where you have separate subId and Endpoint
	    mergedEndpoint = pushSubscription.endpoint + '/' + pushSubscription.subscriptionId;
	  }
	  return mergedEndpoint;
	}

	function getQueryVariable(variable) {
	  var query = window.location.search.substring(1);
	  var vars = query.split('&');
	  for (var i = 0; i < vars.length; i++) {
	    var pair = vars[i].split('=');
	    if (pair[0] == variable) {
	      return pair[1];
	    }
	  }
	  return undefined;
	}

	function getQueryVariables() {
	  var variablesArray = arguments.length <= 0 || arguments[0] === undefined ? [] : arguments[0];

	  var params = {};
	  variablesArray.forEach(function (param) {
	    params[param] = getQueryVariable(param);
	  });
	  return params;
	}

	function validateObject(obj) {
	  var keyArray = arguments.length <= 1 || arguments[1] === undefined ? [] : arguments[1];

	  var ok = true;
	  keyArray.forEach(function (key) {
	    if (!obj[key]) {
	      ok = false;
	    }
	  });
	  return ok;
	}

	function storeSubscriptionLocallyAndCompare(newSubscription, callbackWhenDifferent) {
	  newSubscription = newSubscription || {};
	  var newEndpoint = newSubscription.endpoint;

	  try {
	    var storage = window.localStorage;
	    if (storage) {
	      var currentKnownSubscription = JSON.parse(storage.getItem('currentKnownSubscription') || '{}') || {};
	      var oldEndpoint = currentKnownSubscription.endpoint;

	      // if the new subscription is different from the one we registered
	      if (oldEndpoint && oldEndpoint != newEndpoint) {
	        callbackWhenDifferent(currentKnownSubscription);
	      }

	      // register the new subscription
	      localStorage.setItem('currentKnownSubscription', JSON.stringify(newSubscription));
	    }
	  } catch (e) {}
	}

	// do nothing

/***/ }
/******/ ]);