export default function (view) {
  view.addEventListener("viewshow", () => import(
    window.ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(0);

    Dashboard.showLoadingMsg();
    ApiClient.getPluginConfiguration(pluginId).then(function (config) {
      view.querySelector('#BaseUrl').value = config.BaseUrl;
      view.querySelector('#Username').value = config.Username;
      view.querySelector('#Password').value = config.Password;
      view.querySelector('#UserAgent').value = config.UserAgent;
      view.querySelector('#MyTimezone').value = config.MyTimezone;

      // Multiplexer settings
      view.querySelector('#EnableMultiplexing').checked = config.EnableMultiplexing || false;
      view.querySelector('#MaxActiveConnections').value = config.MaxActiveConnections || 1;
      view.querySelector('#MultiplexSliceSeconds').value = config.MultiplexSliceSeconds || 3;
      view.querySelector('#MultiplexRetentionSeconds').value = config.MultiplexRetentionSeconds || 120;

      // Populate EPG sources
      const tbody = view.querySelector('#EpgSourcesBody');
      tbody.innerHTML = '';
      (config.EpgSources || []).forEach(src => addEpgSourceRow(tbody, src.Id, src.Name, src.Url));

      Dashboard.hideLoadingMsg();
    });

    const reloadStatus = () => {
      const status = view.querySelector("#ProviderStatus");
      const expiry = view.querySelector("#ProviderExpiry");
      const cons = view.querySelector("#ProviderConnections");
      const maxCons = view.querySelector("#ProviderMaxConnections");
      const time = view.querySelector("#ProviderTime");
      const timezone = view.querySelector("#ProviderTimezone");
      const mpegTs = view.querySelector("#ProviderMpegTs");

      Xtream.fetchJson('Xtream/TestProvider').then(response => {
        status.innerText = response.Status;
        expiry.innerText = response.ExpiryDate;
        cons.innerText = response.ActiveConnections;
        maxCons.innerText = response.MaxConnections;
        time.innerText = response.ServerTime;
        timezone.innerText = response.ServerTimezone;
        mpegTs.innerText = response.SupportsMpegTs;
      }).catch((_) => {
        status.innerText = "Failed. Check server logs.";
        expiry.innerText = "";
        cons.innerText = "";
        maxCons.innerText = "";
        time.innerText = "";
        timezone.innerText = "";
        mpegTs.innerText = "";
      });
    };
    reloadStatus();

    view.querySelector('#UserAgentFromBrowser').addEventListener('click', (e) => {
      e.preventDefault();
      view.querySelector('#UserAgent').value = navigator.userAgent;
    });

    const addEpgSourceRow = (tbody, id, name, url) => {
      const tr = document.createElement('tr');
      tr.dataset.id = id || crypto.randomUUID();
      tr.innerHTML = `
        <td><input type="text" class="epg-name" value="${name || ''}" is="emby-input" style="width:100%" /></td>
        <td><input type="text" class="epg-url" value="${url || ''}" is="emby-input" style="width:100%" /></td>
        <td><button type="button" is="emby-button" class="raised epg-remove">Remove</button></td>
      `;
      tr.querySelector('.epg-remove').addEventListener('click', () => tr.remove());
      tbody.appendChild(tr);
    };

    view.querySelector('#AddEpgSource').addEventListener('click', (e) => {
      e.preventDefault();
      addEpgSourceRow(view.querySelector('#EpgSourcesBody'), '', '', '');
    });

    view.querySelector('#XtreamCredentialsForm').addEventListener('submit', (e) => {
      Dashboard.showLoadingMsg();

      ApiClient.getPluginConfiguration(pluginId).then((config) => {
        config.BaseUrl = view.querySelector('#BaseUrl').value;
        config.Username = view.querySelector('#Username').value;
        config.Password = view.querySelector('#Password').value;
        config.UserAgent = view.querySelector('#UserAgent').value;
        config.MyTimezone = view.querySelector('#MyTimezone').value;

        // Multiplexer settings
        config.EnableMultiplexing = view.querySelector('#EnableMultiplexing').checked;
        config.MaxActiveConnections = parseInt(view.querySelector('#MaxActiveConnections').value, 10) || 1;
        config.MultiplexSliceSeconds = parseInt(view.querySelector('#MultiplexSliceSeconds').value, 10) || 3;
        config.MultiplexRetentionSeconds = parseInt(view.querySelector('#MultiplexRetentionSeconds').value, 10) || 120;

        // Collect EPG sources from table
        config.EpgSources = [];
        view.querySelectorAll('#EpgSourcesBody tr').forEach(tr => {
          const name = tr.querySelector('.epg-name').value.trim();
          const url = tr.querySelector('.epg-url').value.trim();
          if (name && url) {
            config.EpgSources.push({ Id: tr.dataset.id, Name: name, Url: url });
          }
        });

        ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
          reloadStatus();
          Dashboard.processPluginConfigurationUpdateResult(result);
        });
      });

      e.preventDefault();
      return false;
    });
  }));
}