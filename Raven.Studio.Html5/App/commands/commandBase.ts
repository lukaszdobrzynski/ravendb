import raven = require("common/raven");
import alertArgs = require("common/alertArgs");
import alertType = require("common/alertType");
import database = require("models/database");
import appUrl = require("common/appUrl");

/// Commands encapsulate a read or write operation to the database and support progress notifications and common AJAX related functionality.
class commandBase {
    private baseUrl = "http://localhost:8080"; // For debugging purposes, uncomment this line to point Raven at an already-running Raven server. Requires the Raven server to have it's config set to <add key="Raven/AccessControlAllowOrigin" value="*" />
    //private baseUrl = ""; // This should be used when serving HTML5 Studio from the server app.

    // TODO: better place for this?
    static ravenClientVersion = '3.0.0.0';

    constructor() {
    }

    execute<T>(): JQueryPromise<T> {
        throw new Error("Execute must be overridden.");
    }

    reportInfo(title: string, details?: string) {
        this.reportProgress(alertType.info, title, details);
    }

    reportError(title: string, details?: string) {
        this.reportProgress(alertType.danger, title, details);
    }

    reportSuccess(title: string, details?: string) {
        this.reportProgress(alertType.success, title, details);
    }

    reportWarning(title: string, details?: string) {
        this.reportProgress(alertType.warning, title, details);
    }

    query<T>(relativeUrl: string, args: any, database?: database, resultsSelector?: (results: any) => T): JQueryPromise<T> {
        var ajax = this.ajax(relativeUrl, args, "GET", database);
        if (resultsSelector) {
            var task = $.Deferred();
            ajax.done((results, status, xhr) => {
                var transformedResults = resultsSelector(results);
                task.resolve(transformedResults);
            });
            ajax.fail((request, status, error) => task.reject(request, status, error));
            return task;
        } else {
            return ajax;
        }
    }

    put(relativeUrl: string, args: any, database?: database, customHeaders?: any): JQueryPromise<any> {
        return this.ajax(relativeUrl, args, "PUT", database, customHeaders);
    }

    /*
     * Performs a DELETE rest call.
    */
    del(relativeUrl: string, args: any, database?: database, customHeaders?: any): JQueryPromise<any> {
        return this.ajax(relativeUrl, args, "DELETE", database, customHeaders);
    }

    post(relativeUrl: string, args: any, database?: database, customHeaders?: any): JQueryPromise<any> {
        return this.ajax(relativeUrl, args, "POST", database, customHeaders);
    }

    private ajax(relativeUrl: string, args: any, method: string, database?: database, customHeaders?: any): JQueryPromise<any> {
        // ContentType:
        //
        // Can't use application/json in cross-domain requests, otherwise it 
        // issues OPTIONS prefligh request first, which doesn't return proper 
        // headers(e.g.Etag header, etc.)
        // 
        // So, for GETs, we issue text/plain requests, which skip the OPTIONS
        // request and goes straight for the GET request.
        var contentType = method === "GET" ?
            "text/plain; charset=utf-8" :
            "application/json; charset=utf-8";
        var options = {
            cache: false,
            url: appUrl.forDatabaseQuery(database) + relativeUrl,
            data: args,
            contentType: contentType, 
            type: method,
            headers: undefined
        };

        if (customHeaders) {
            options.headers = customHeaders;
        }

        return $.ajax(options);
    }

    private reportProgress(type: alertType, title: string, details?: string) {
        ko.postbox.publish("Alert", new alertArgs(type, title, details));
    }
}

export = commandBase;