// The status widget's only script: the map picture's address is in data-art, set through CSSOM (the CSP forbids style=)
(function () {
    document.querySelectorAll(".card-art[data-art]").forEach(function (art) {
        var url = art.dataset.art;
        if (url && /^\/(media|maps)\/[^"'()\\]+$/.test(url)) art.style.backgroundImage = 'url("' + url + '")';
    });
})();
